using Companion.Core.Abstractions;
using Companion.Core.Domain;

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
    }
}
