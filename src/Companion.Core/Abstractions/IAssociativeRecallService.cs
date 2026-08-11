using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IAssociativeRecallService
{
    Task<IReadOnlyList<RetrievalResult>> ExpandAsync(string userId, string query, IReadOnlyList<RetrievalResult> primary, int limit, CancellationToken ct = default);
}
