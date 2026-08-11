using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>Deterministic reranker: preserves the retriever's existing ranking.</summary>
public sealed class RuleBasedMemoryReranker : IMemoryReranker
{
    public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query, IReadOnlyList<RetrievalResult> candidates, int maxResults,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RetrievalResult>>(candidates.Take(maxResults).ToList());
}
