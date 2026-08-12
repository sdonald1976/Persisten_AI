using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// The bounded tool-use loop, driven by the executive TOOL PLANNER — a model role that is
/// deliberately not the companion (no personality, no prose, no reply): its only job is
/// deciding what she should look up before the conversational model answers. Three tiers:
/// deterministic nudges execute the extremely-clear cases outright; the planner (given a
/// COMPACT context — recent messages, retrieval summary, hints — never the full packet) returns
/// a structured plan of zero or more calls; a second planning round may follow up on results.
/// Deterministic code still decides everything that matters: whether a tool exists and is
/// available, whether arguments validate, which user it runs as (always the trusted one, never
/// model-supplied). Hard bounds throughout: max calls per turn, max planning rounds,
/// identical-call dedupe, per-call timeout, bounded result sizes. Planner output is untrusted;
/// planner failure is benign — the turn simply proceeds without lookups.
/// </summary>
public sealed class ToolLoop
{
    /// <summary>Planning passes per turn: an initial plan plus one follow-up on its results.</summary>
    public const int MaxPlanningRounds = 2;
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
        IReadOnlyList<string> Decisions,
        int PlanningRounds);

    public async Task<Outcome> RunAsync(
        string userId, string planningContext, string userMessage, CancellationToken ct = default)
    {
        var available = _tools.Where(t => t.Available).ToList();
        var advertised = available.Select(t => t.Name).ToList();
        if (!_options.EnableToolUse || available.Count == 0)
            return new Outcome(advertised, Array.Empty<ToolCallTrace>(), null, Array.Empty<string>(), 0);

        var traces = new List<ToolCallTrace>();
        var resultBlocks = new List<string>();
        var decisions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var budget = Math.Max(1, _options.MaxToolCallsPerTurn);
        var executed = 0;

        // Tier 1 — deterministic nudges (the intent-parser philosophy applied to lookups): an
        // extremely clear phrasing — "can you see images?", "why did you say that?" — selects
        // its tool without any model judgment, so the obvious cases work even when every model
        // declines. The match is also handed to the planner below as a hint for anything more.
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
                executed++;
            }
        }

        // Tier 2 — the executive planner: a structured plan of zero or more calls per round,
        // with one optional follow-up round to chase what the first round's results revealed.
        var rounds = 0;
        for (var round = 0; round < MaxPlanningRounds && executed < budget; round++)
        {
            ct.ThrowIfCancellationRequested();

            string raw;
            try
            {
                rounds++;
                raw = (await _chat.CompleteAsync(
                    PlannerPrompt(planningContext, available, nudge, resultBlocks),
                    userMessage, jsonMode: true, ct: ct)).Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Planner failure is benign by contract: record it, answer without tools.
                decisions.Add($"(planner failure) {ex.GetType().Name}");
                _logger.LogDebug(ex, "Tool planner call failed; answering without further tools.");
                break;
            }

            // The verbatim plan (clipped) goes to diagnostics: "did it decline or ramble?" is
            // the first question when tools never fire, and it must be answerable from data.
            decisions.Add(Clip(raw.Trim(), 300));

            var plan = TryParsePlan(raw);
            if (plan is null)
            {
                decisions.Add("(planner output unusable — answering without further tools)");
                break;
            }
            if (plan.Count == 0)
                break; // needsTools: false — the honest, common case

            var executedThisRound = 0;
            foreach (var call in plan)
            {
                if (executed >= budget)
                    break;

                var tool = available.FirstOrDefault(
                    t => t.Name.Equals(call.Tool, StringComparison.OrdinalIgnoreCase));
                if (tool is null)
                {
                    // Asked for something this installation doesn't have — record the truth,
                    // skip it, and let any remaining valid calls proceed.
                    traces.Add(new ToolCallTrace
                    {
                        Tool = call.Tool, Arguments = call.ArgumentsJson,
                        Ok = false, Code = "unavailable",
                    });
                    await _diagnostics.RecordToolCallAsync(new ToolCallRecord
                    {
                        Id = Guid.NewGuid(), UserId = userId, Tool = call.Tool,
                        Ok = false, Code = "unavailable", Timestamp = _clock.GetUtcNow(),
                    }, CancellationToken.None);
                    continue;
                }

                // The same call twice can only mean a loop — the result won't change.
                if (!seen.Add(tool.Name + "|" + call.ArgumentsJson))
                    continue;

                await ExecuteAndRecordAsync(
                    tool, call.Arguments, call.ArgumentsJson, userId, traces, resultBlocks, ct);
                executed++;
                executedThisRound++;
            }

            // A round that ran nothing new can only repeat itself — stop planning.
            if (executedThisRound == 0)
                break;
        }

        var section = resultBlocks.Count == 0 ? null : Clip(string.Join("\n", resultBlocks), MaxSectionChars);
        return new Outcome(advertised, traces, section, decisions, rounds);
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

    private static string PlannerPrompt(
        string planningContext, IReadOnlyList<ICompanionTool> tools,
        ToolNudge.Match? hint, IReadOnlyList<string> resultsSoFar)
    {
        var sb = new StringBuilder(Prompts.Get("planner.system"));
        sb.AppendLine().AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.Name}: {tool.Description} Arguments: {tool.ArgumentsHint}");
        sb.AppendLine();
        sb.AppendLine(planningContext);
        if (hint is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Deterministic hint (already executed when its results appear below): {hint.Tool}");
        }
        if (resultsSoFar.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Results you already looked up (do not repeat these calls):");
            foreach (var block in resultsSoFar)
                sb.AppendLine(block);
        }
        return sb.ToString();
    }

    private sealed record PlannedCall(string Tool, JsonElement Arguments, string ArgumentsJson);

    /// <summary>
    /// Parses a planner response into zero or more calls. Accepts the structured plan
    /// ({"needsTools": …, "calls": […]}) and, for robustness with small models, the legacy
    /// single-call dialect ({"tool": "name", …} / {"tool": null}). Null = unusable output.
    /// </summary>
    private static IReadOnlyList<PlannedCall>? TryParsePlan(string raw)
    {
        var text = StripFence(raw).Trim();
        if (text.Length == 0 || text[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Legacy single-call dialect.
            if (root.TryGetProperty("tool", out var toolProp))
            {
                if (toolProp.ValueKind != JsonValueKind.String)
                    return Array.Empty<PlannedCall>(); // {"tool": null} — answer directly
                var single = ParseOneCall(root);
                return single is null ? Array.Empty<PlannedCall>() : new[] { single };
            }

            // Structured plan.
            if (root.TryGetProperty("needsTools", out var needs)
                && needs.ValueKind == JsonValueKind.False)
                return Array.Empty<PlannedCall>();

            if (!root.TryGetProperty("calls", out var callsProp)
                || callsProp.ValueKind != JsonValueKind.Array)
                return root.TryGetProperty("needsTools", out _) ? Array.Empty<PlannedCall>() : null;

            var calls = new List<PlannedCall>();
            foreach (var element in callsProp.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;
                var call = ParseOneCall(element);
                if (call is not null)
                    calls.Add(call);
            }
            return calls;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlannedCall? ParseOneCall(JsonElement element)
    {
        if (!element.TryGetProperty("tool", out var toolProp) || toolProp.ValueKind != JsonValueKind.String)
            return null;
        var tool = toolProp.GetString();
        if (string.IsNullOrWhiteSpace(tool))
            return null;

        var args = element.TryGetProperty("arguments", out var argsProp)
            && argsProp.ValueKind == JsonValueKind.Object
                ? argsProp.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
        return new PlannedCall(tool!, args, args.GetRawText());
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
