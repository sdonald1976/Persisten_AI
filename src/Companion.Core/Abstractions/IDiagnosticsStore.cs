using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Durable operational telemetry: every model call and every tool call, persisted so "which
/// model is slow", "does she actually use her tools", and "what failed last night" are
/// answerable from data instead of memory. Writes must NEVER throw into a caller — telemetry
/// failing is a log line, not a broken turn — and implementations own that guarantee.
/// </summary>
public interface IDiagnosticsStore
{
    Task RecordModelCallAsync(ModelCallRecord record, CancellationToken ct = default);

    Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct = default);

    /// <summary>Most recent tool calls for the user, newest first.</summary>
    Task<IReadOnlyList<ToolCallRecord>> GetRecentToolCallsAsync(
        string userId, int count, CancellationToken ct = default);

    /// <summary>Per role+model aggregates over calls since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<ModelRoleStats>> GetModelStatsAsync(
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Deletes records older than the cutoff; returns how many went.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
