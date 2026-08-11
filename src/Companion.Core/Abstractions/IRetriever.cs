using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Retrieves a ranked, explained set of memories relevant to a query.</summary>
public interface IRetriever
{
    /// <param name="detectedProject">
    /// The project the turn resolved to (from <see cref="IEntityResolver"/>), used to boost
    /// project-associated memories. Null when no project was confidently resolved.
    /// </param>
    Task<RetrievalOutcome> RetrieveAsync(
        string userId, string query, string? detectedProject = null, CancellationToken ct = default);
}

/// <summary>The selected memories and the ones scored but excluded (for diagnostics).</summary>
public sealed record RetrievalOutcome
{
    public required IReadOnlyList<RetrievalResult> Selected { get; init; }
    public required IReadOnlyList<RetrievalResult> Excluded { get; init; }

    /// <summary>
    /// The query's embedding, exposed so other similarity lookups this turn (e.g. resurfacing a
    /// relevant past musing) reuse it instead of paying a second embedding call. Null when
    /// retrieval bailed before embedding (no memories at all).
    /// </summary>
    public float[]? QueryEmbedding { get; init; }
}
