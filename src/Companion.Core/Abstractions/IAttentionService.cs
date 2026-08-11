using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IAttentionService
{
    Task CaptureTurnAsync(string userId, Message message, bool remember, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SelectForContextAsync(string userId, string query, int limit, CancellationToken ct = default);
}
