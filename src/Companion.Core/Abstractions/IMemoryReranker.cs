using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Optional second-pass relevance judge for memory retrieval. The retriever produces an explainable
/// hybrid ranking first; a reranker may reorder or drop those candidates for the exact turn.
/// </summary>
public interface IMemoryReranker
{
    Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query, IReadOnlyList<RetrievalResult> candidates, int maxResults,
        CancellationToken ct = default);
}
