using System.Text;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Api;

/// <summary>Operational diagnostics: what actually ran (turns, tool calls, model telemetry).</summary>
internal static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        // ---- operational diagnostics: what actually ran (turns, tools, model telemetry) ----

        // The in-memory ring: the last few turns' full operational story (sections, retrieval, tools).
        app.MapGet("/diagnostics/turns", (IUserContext user, ITurnTraceLog log) =>
            Results.Ok(log.Recent(user.UserId, 5)));

        // ---- what the model was actually given ----
        //
        // The exact rendered system prompt and user message per turn. Requires
        // Companion:CapturePromptText=true; without it the text fields are null and only the
        // sizes are shown, which is the honest failure mode — an empty prompt view is better
        // than a plausible-looking reconstruction.

        // Table view: one row per recent turn, with the full text on each.
        app.MapGet("/diagnostics/prompt", (
            IUserContext user, ITurnTraceLog log, IOptions<CompanionOptions> options, int? count) =>
        {
            var captured = options.Value.CapturePromptText;
            var turns = log.Recent(user.UserId, Math.Clamp(count ?? 5, 1, 50));
            return Results.Ok(new
            {
                capturing = captured,
                note = captured
                    ? "systemPrompt is the exact text handed to the conversation model."
                    : "Set Companion:CapturePromptText=true and restart to capture the text; sizes are shown regardless.",
                turns = turns.Select(t => new
                {
                    t.TraceId,
                    t.At,
                    t.ModelUsed,
                    promptChars = t.PromptChars,
                    sections = t.ContextSections,
                    systemPrompt = t.PromptSystem,
                    userMessage = t.PromptUser ?? t.UserMessagePreview,
                }),
            });
        });

        // Plain-text view of one turn — the whole thing, exactly as sent, nothing escaped.
        // Defaults to the most recent turn; pass ?trace=<guid> for a specific one.
        app.MapGet("/diagnostics/prompt/text", (
            IUserContext user, ITurnTraceLog log, IOptions<CompanionOptions> options, Guid? trace) =>
        {
            if (!options.Value.CapturePromptText)
                return Results.Text(
                    "Prompt capture is off. Set Companion:CapturePromptText=true and restart.",
                    "text/plain");

            var turns = log.Recent(user.UserId, 50);
            var turn = trace is { } id
                ? turns.FirstOrDefault(t => t.TraceId == id)
                : turns.FirstOrDefault(t => t.PromptSystem is not null);

            if (turn?.PromptSystem is null)
                return Results.Text(
                    "No captured prompt for that turn yet — send a message and try again.",
                    "text/plain");

            var text = new StringBuilder()
                .AppendLine($"=== turn {turn.TraceId} at {turn.At:u} ===")
                .AppendLine($"=== model: {turn.ModelUsed ?? "(unrecorded)"} | system prompt: {turn.PromptChars} chars ===")
                .AppendLine()
                .AppendLine("----- SYSTEM PROMPT (exactly as sent) -----")
                .AppendLine(turn.PromptSystem)
                .AppendLine()
                .AppendLine("----- USER MESSAGE (exactly as sent) -----")
                .AppendLine(turn.PromptUser ?? "(not captured)")
                .ToString();

            return Results.Text(text, "text/plain");
        });

        // Durable turn history, newest first: the decision evidence that survives a restart —
        // working-context reading, intent, retrieval with scores, decisions. Bounded previews;
        // private turns keep structure only.
        app.MapGet("/diagnostics/turns/history", async (
            IUserContext user, IDiagnosticsStore diagnostics, int? count, CancellationToken ct) =>
            Results.Ok(await diagnostics.GetRecentTurnsAsync(user.UserId, count ?? 20, ct)));

        // Durable tool-call history, newest first: when and whether she used her tools.
        app.MapGet("/diagnostics/tools", async (
            IUserContext user, IDiagnosticsStore diagnostics, int? count, CancellationToken ct) =>
            Results.Ok(await diagnostics.GetRecentToolCallsAsync(user.UserId, count ?? 50, ct)));

        // Per role+model aggregates (calls, failures, latency, tokens) over a window — the data for
        // deciding which model earns which job.
        app.MapGet("/diagnostics/models", async (
            IDiagnosticsStore diagnostics, TimeProvider clock, double? hours, CancellationToken ct) =>
        {
            var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 0.1, 24 * 90));
            return Results.Ok(await diagnostics.GetModelStatsAsync(clock.GetUtcNow() - window, ct));
        });

        // The specialist (ONNX) models, which are a different question from the generative roster
        // above: not "how is it performing" but "is it even here". Every one is optional and every
        // one falls back silently by design, so without somewhere to look, a model that quietly
        // failed to load is indistinguishable from one that is working.
        app.MapGet("/diagnostics/cognitive", (
            ITextPairScorer reranker, INliModel nli, ITextClassifier classifier) =>
            Results.Ok(new[] { reranker.Status, nli.Status, classifier.Status }));

        // How often a shadowed model disagrees with the heuristic it might replace, and how much
        // latency it adds. The two questions that decide a promotion.
        app.MapGet("/diagnostics/shadow", async (
            IShadowRecorder shadow, TimeProvider clock, double? hours, CancellationToken ct) =>
        {
            var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24 * 7, 0.1, 24 * 90));
            return Results.Ok(await shadow.GetAgreementAsync(clock.GetUtcNow() - window, ct));
        });

        // The disagreements themselves — the queue of cases worth a human deciding who was right,
        // which is the only thing that turns an agreement rate into evidence.
        app.MapGet("/diagnostics/shadow/disagreements", async (
            IShadowRecorder shadow, string? subject, int? count, CancellationToken ct) =>
            Results.Ok(await shadow.GetDisagreementsAsync(subject, count ?? 50, ct)));

        // Captured judgements: what the heuristics said about real sentences, with no model
        // involved. This is the export that turns a synthetic corpus into a real one — see
        // training/cognition/harvest.py, which reads this endpoint and writes a review queue.
        app.MapGet("/diagnostics/shadow/captures", async (
            IShadowRecorder shadow, string? subject, int? count, CancellationToken ct) =>
            Results.Ok(await shadow.GetCapturesAsync(subject, count ?? 500, ct)));

        // The renderer shadow's queue lifecycle (docs/RENDERER_SHADOW.md) beside its collected
        // row counts — queued/completed/failed/dropped say whether the instrument is healthy,
        // clean/flagged say how the collection toward the 100-turn target is going. When the
        // user-scoped canary is on, activeRenderer names what the canary user actually hears,
        // and the adapter sha identifies exactly which weights that is.
        app.MapGet("/diagnostics/renderer-shadow", async (
            IRendererShadow renderer, IShadowRecorder shadow, IUserContext user,
            Microsoft.Extensions.Options.IOptions<Companion.Core.CompanionOptions> options,
            TimeProvider clock, CancellationToken ct) =>
        {
            var agreement = await shadow.GetAgreementAsync(clock.GetUtcNow() - TimeSpan.FromDays(90), ct);
            var rows = agreement.FirstOrDefault(a => a.Subject == "renderer.plan2");
            var rs = options.Value.RendererShadow;
            var canary = renderer.IsCanaryFor(user.UserId);
            var counters = renderer.Counters;
            return Results.Ok(new
            {
                observing = renderer.IsObserving,
                activeRenderer = canary ? "run-1c (user-scoped canary, production fallback)" : "production",
                canaryUser = canary ? user.UserId : null,
                adapterSha256 = rs.AdapterSha256,
                modelVersion = rs.ModelVersion,
                queue = counters,
                // P3: translated_v2 V3 shadow lifecycle (produced/valid/invalid/compatible/
                // protected/redacted/failed/dropped). These rows test translation,
                // serialization, privacy, and infrastructure — never corpus material.
                v3Shadow = counters.V3,
                collected = rows?.Comparisons ?? 0,
                flagged = rows?.Disagreements ?? 0,
                averageLatencyMs = rows?.AverageDurationMs ?? 0,
            });
        });
    }
}
