using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// The bounded tool-use loop: before her final reply, the chat model may look things up through
/// the registered tools. Each iteration asks ONE question — "would a tool genuinely help answer
/// this message, given what you already have?" — as strict JSON; deterministic code then decides
/// everything else: whether the tool exists and is available, whether the arguments validate,
/// which user it runs as (always the trusted one, never model-supplied). Hard bounds throughout:
/// max calls per turn, identical-call dedupe, per-call timeout, bounded result sizes. The model's
/// output is untrusted input; the worst it can achieve is a few wasted read-only lookups.
/// </summary>
public sealed class ToolLoop
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Per-call execution timeout; every tool here is a local lookup.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    private const int MaxResultChars = 2000;
    private const int MaxSectionChars = 6000;

    private readonly IEnumerable<ICompanionTool> _tools;
    private readonly IChatModel _chat;
    private readonly IDiagnosticsStore _diagnostics;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ToolLoop> _logger;

    public ToolLoop(
        IEnumerable<ICompanionTool> tools, IChatModel chat, IDiagnosticsStore diagnostics,
        IOptions<CompanionOptions> options, TimeProvider clock, ILogger<ToolLoop> logger)
    {
        _tools = tools;
        _chat = chat;
        _diagnostics = diagnostics;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public sealed record Outcome(
        IReadOnlyList<string> AdvertisedTools,
        IReadOnlyList<ToolCallTrace> Calls,
        string? ResultsSection,
        IReadOnlyList<string> Decisions);

    public async Task<Outcome> RunAsync(
        string userId, string renderedContext, string userMessage, CancellationToken ct = default)
    {
        var available = _tools.Where(t => t.Available).ToList();
        var advertised = available.Select(t => t.Name).ToList();
        if (!_options.EnableToolUse || available.Count == 0)
            return new Outcome(advertised, Array.Empty<ToolCallTrace>(), null, Array.Empty<string>());

        var traces = new List<ToolCallTrace>();
        var resultBlocks = new List<string>();
        var decisions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 0. Rules first (the intent-parser philosophy, applied to lookups): an unambiguous
        // phrasing — "can you see images?", "why did you say that?" — selects its tool
        // deterministically and runs BEFORE the model is consulted, so the obvious cases work
        // even when a small model would politely decline and then confabulate. The model loop
        // below still runs for anything the rules can't see (dedupe prevents a repeat call).
        var nudge = ToolNudge.Detect(userMessage);
        if (nudge is not null)
        {
            var nudged = available.FirstOrDefault(
                t => t.Name.Equals(nudge.Tool, StringComparison.OrdinalIgnoreCase));
            if (nudged is not null && seen.Add(nudged.Name + "|" + nudge.ArgumentsJson))
            {
                decisions.Add($"(rule nudge) {nudged.Name} {nudge.ArgumentsJson}");
                using var args = JsonDocument.Parse(nudge.ArgumentsJson);
                await ExecuteAndRecordAsync(
                    nudged, args.RootElement, nudge.ArgumentsJson, userId, traces, resultBlocks, ct);
            }
        }

        for (var i = 0; i < Math.Max(1, _options.MaxToolCallsPerTurn); i++)
        {
            ct.ThrowIfCancellationRequested();

            string raw;
            try
            {
                raw = (await _chat.CompleteAsync(
                    DecisionPrompt(renderedContext, available, resultBlocks), userMessage, jsonMode: true, ct: ct)).Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Tool decision call failed; answering without tools.");
                break;
            }

            // The verbatim decision (clipped) goes to diagnostics: "did she decline or ramble?"
            // is the first question when tools never fire, and it must be answerable from data.
            decisions.Add(Clip(raw.Trim(), 200));

            var call = TryParseCall(raw);
            if (call is null)
                break; // "answer directly" (or unusable output — same safe outcome)

            var tool = available.FirstOrDefault(
                t => t.Name.Equals(call.Value.Tool, StringComparison.OrdinalIgnoreCase));
            if (tool is null)
            {
                // Asked for something this installation doesn't have — record the truth and stop.
                traces.Add(new ToolCallTrace
                {
                    Tool = call.Value.Tool, Arguments = call.Value.ArgumentsJson,
                    Ok = false, Code = "unavailable",
                });
                await _diagnostics.RecordToolCallAsync(new ToolCallRecord
                {
                    Id = Guid.NewGuid(), UserId = userId, Tool = call.Value.Tool,
                    Ok = false, Code = "unavailable", Timestamp = _clock.GetUtcNow(),
                }, CancellationToken.None);
                break;
            }

            // The same call twice can only mean a loop — the result won't change.
            if (!seen.Add(tool.Name + "|" + call.Value.ArgumentsJson))
                break;

            await ExecuteAndRecordAsync(
                tool, call.Value.Arguments, call.Value.ArgumentsJson, userId, traces, resultBlocks, ct);
        }

        var section = resultBlocks.Count == 0 ? null : Clip(string.Join("\n", resultBlocks), MaxSectionChars);
        return new Outcome(advertised, traces, section, decisions);
    }

    /// <summary>One mediated tool execution: timeout, trace, durable record, result block.</summary>
    private async Task ExecuteAndRecordAsync(
        ICompanionTool tool, JsonElement arguments, string argumentsJson, string userId,
        List<ToolCallTrace> traces, List<string> resultBlocks, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ToolTimeout);
            try
            {
                result = await tool.ExecuteAsync(userId, arguments, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = ToolResult.Fail("timeout", "The lookup took too long.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tool {Tool} failed.", tool.Name);
                result = ToolResult.Fail("provider_failure", "The lookup failed.");
            }
        }
        stopwatch.Stop();

        var resultJson = Clip(JsonSerializer.Serialize(
            new { ok = result.Ok, code = result.Code, data = result.Data }, Json), MaxResultChars);

        traces.Add(new ToolCallTrace
        {
            Tool = tool.Name,
            Arguments = argumentsJson,
            Ok = result.Ok,
            Code = result.Code,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ResultSummary = resultJson,
        });
        resultBlocks.Add($"[{tool.Name}] {resultJson}");

        // The durable record (the in-memory ring forgets on restart; this doesn't).
        await _diagnostics.RecordToolCallAsync(new ToolCallRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Tool = tool.Name,
            Arguments = argumentsJson,
            Ok = result.Ok,
            Code = result.Code,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Timestamp = _clock.GetUtcNow(),
        }, CancellationToken.None);

        _logger.LogInformation(
            "Tool {Tool} for {UserId}: {Code} in {Ms}ms", tool.Name, userId, result.Code, stopwatch.ElapsedMilliseconds);
    }

    private static string DecisionPrompt(
        string renderedContext, IReadOnlyList<ICompanionTool> tools, IReadOnlyList<string> resultsSoFar)
    {
        var sb = new StringBuilder(renderedContext);
        sb.AppendLine().AppendLine();
        sb.AppendLine(Prompts.Get("tools.system"));
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.Name}: {tool.Description} Arguments: {tool.ArgumentsHint}");
        if (resultsSoFar.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Results you already looked up (do not repeat these calls):");
            foreach (var block in resultsSoFar)
                sb.AppendLine(block);
        }
        return sb.ToString();
    }

    private static (string Tool, JsonElement Arguments, string ArgumentsJson)? TryParseCall(string raw)
    {
        var text = StripFence(raw).Trim();
        if (text.Length == 0 || text[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("tool", out var toolProp)
                || toolProp.ValueKind != JsonValueKind.String)
                return null;
            var tool = toolProp.GetString();
            if (string.IsNullOrWhiteSpace(tool))
                return null;

            var args = doc.RootElement.TryGetProperty("arguments", out var argsProp)
                && argsProp.ValueKind == JsonValueKind.Object
                    ? argsProp.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();
            return (tool!, args, args.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var nl = t.IndexOf('\n');
        if (nl < 0)
            return t;
        t = t[(nl + 1)..];
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return end >= 0 ? t[..end] : t;
    }

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
