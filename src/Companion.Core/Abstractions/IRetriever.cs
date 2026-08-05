using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Retrieves a ranked, explained set of memories relevant to a query.</summary>
public interface IRetriever
{
    Task<RetrievalOutcome> RetrieveAsync(string userId, string query, CancellationToken ct = default);
}

/// <summary>
/// The selected memories, the ones scored but excluded (for diagnostics), and any project
/// the retriever inferred from the query. Detection lives here because the retriever already
/// has the user's memories loaded.
/// </summary>
public sealed record RetrievalOutcome
{
    public required IReadOnlyList<RetrievalResult> Selected { get; init; }
    public required IReadOnlyList<RetrievalResult> Excluded { get; init; }
    public string? DetectedProject { get; init; }
}
