using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists unresolved ambiguities so a turn can pause for clarification and a later turn can
/// resume it — surviving an application restart. User-scoped like every other store.
/// </summary>
public interface IPendingClarificationStore
{
    Task AddAsync(PendingClarification pending, CancellationToken ct = default);

    /// <summary>The most recent still-<see cref="ClarificationStatus.Pending"/> item for a conversation, if any.</summary>
    Task<PendingClarification?> GetActiveAsync(string userId, Guid conversationId, CancellationToken ct = default);

    Task UpdateAsync(PendingClarification pending, CancellationToken ct = default);
}
