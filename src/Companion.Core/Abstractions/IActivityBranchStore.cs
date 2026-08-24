using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The shadow-isolated activity store (Source 1b §4). Transactional, idempotent, and
/// entirely separate from the production procedure definitions: writing here can never
/// alter a Procedure row, a displayed reply, memory, or V2 state.
/// </summary>
public interface IActivityBranchStore
{
    /// <summary>
    /// Applies one transition under optimistic concurrency. A duplicate
    /// <paramref name="idempotencyKey"/> returns the EXISTING record unchanged
    /// (<see cref="BranchWriteResult.Duplicate"/>) rather than applying twice.
    /// </summary>
    Task<BranchWriteResult> UpsertAsync(
        ActivityBranchRecord record, string idempotencyKey, CancellationToken ct = default);

    Task<ActivityBranchRecord?> GetAsync(string branchId, CancellationToken ct = default);

    /// <summary>Branches for one conversation, newest first — diagnostics and resume.</summary>
    Task<IReadOnlyList<ActivityBranchRecord>> GetForConversationAsync(
        string userId, Guid conversationId, CancellationToken ct = default);

    /// <summary>
    /// Removes terminal branches older than <paramref name="terminalAge"/> and any
    /// volatile-retention branch older than <paramref name="volatileAge"/>. Returns how
    /// many went.
    /// </summary>
    Task<int> CleanupAsync(
        DateTimeOffset now, TimeSpan terminalAge, TimeSpan volatileAge, CancellationToken ct = default);

    /// <summary>The /forget promise: removes branches whose stored text matches any excerpt.</summary>
    Task<int> ForgetAsync(IReadOnlyCollection<string> excerpts, CancellationToken ct = default);
}

public sealed record BranchWriteResult(
    ActivityBranchRecord Record, bool Applied, bool Duplicate, string? Conflict)
{
    public static BranchWriteResult Wrote(ActivityBranchRecord r) => new(r, true, false, null);
    public static BranchWriteResult AlreadyApplied(ActivityBranchRecord r) => new(r, false, true, null);
    public static BranchWriteResult Conflicted(ActivityBranchRecord r, string reason)
        => new(r, false, false, reason);
}
