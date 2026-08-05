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
}
