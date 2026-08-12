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
    private readonly CompanionOptions _options;
    private readonly ILogger<ToolLoop> _logger;

    public ToolLoop(
        IEnumerable<ICompanionTool> tools, IChatModel chat,
        IOptions<CompanionOptions> options, ILogger<ToolLoop> logger)
    {
        _tools = tools;
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public sealed record Outcome(
        IReadOnlyList<string> AdvertisedTools,
        IReadOnlyList<ToolCallTrace> Calls,
        string? ResultsSection);

    public async Task<Outcome> RunAsync(
        string userId, string renderedContext, string userMessage, CancellationToken ct = default)
    {
        var available = _tools.Where(t => t.Available).ToList();
        var advertised = available.Select(t => t.Name).ToList();
        if (!_options.EnableToolUse || available.Count == 0)
            return new Outcome(advertised, Array.Empty<ToolCallTrace>(), null);

        var traces = new List<ToolCallTrace>();
        var resultBlocks = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                break;
            }

            // The same call twice can only mean a loop — the result won't change.
            if (!seen.Add(tool.Name + "|" + call.Value.ArgumentsJson))
                break;

            var stopwatch = Stopwatch.StartNew();
            ToolResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ToolTimeout);
                try
                {
                    result = await tool.ExecuteAsync(userId, call.Value.Arguments, timeout.Token);
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
                Arguments = call.Value.ArgumentsJson,
                Ok = result.Ok,
                Code = result.Code,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResultSummary = resultJson,
            });
            resultBlocks.Add($"[{tool.Name}] {resultJson}");

            _logger.LogInformation(
                "Tool {Tool} for {UserId}: {Code} in {Ms}ms", tool.Name, userId, result.Code, stopwatch.ElapsedMilliseconds);
        }

        var section = resultBlocks.Count == 0 ? null : Clip(string.Join("\n", resultBlocks), MaxSectionChars);
        return new Outcome(advertised, traces, section);
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
