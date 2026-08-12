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

    }
}
