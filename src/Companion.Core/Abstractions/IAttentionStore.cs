using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IAttentionStore
{
    Task<IReadOnlyList<AttentionItem>> GetActiveAsync(string userId, int limit, CancellationToken ct = default);
    Task UpsertAsync(AttentionItem item, CancellationToken ct = default);
    Task UpdateAsync(AttentionItem item, CancellationToken ct = default);
    Task ExpireOldAsync(string userId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Removes what the forgotten messages produced here. EXACT message identity only, and
    /// user-scoped by the query so cross-user deletion is structurally impossible. Returns
    /// how many rows changed; forgetting twice returns zero.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default);
}
