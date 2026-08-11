using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IAttentionStore
{
    Task<IReadOnlyList<AttentionItem>> GetActiveAsync(string userId, int limit, CancellationToken ct = default);
    Task UpsertAsync(AttentionItem item, CancellationToken ct = default);
    Task UpdateAsync(AttentionItem item, CancellationToken ct = default);
    Task ExpireOldAsync(string userId, DateTimeOffset now, CancellationToken ct = default);
}
