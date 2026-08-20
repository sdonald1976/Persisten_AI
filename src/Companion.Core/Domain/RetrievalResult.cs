namespace Companion.Core.Domain;

/// <summary>
/// One ranked retrieval hit, with the per-signal score breakdown and a human-readable
/// reason. This is what makes retrieval explainable rather than a black box.
/// </summary>
public sealed record RetrievalResult
{
    public required IMemory Memory { get; init; }

    /// <summary>Final combined score used for ranking.</summary>
    public required double Score { get; init; }

    /// <summary>Individual signal contributions (already weighted), keyed by signal name.</summary>
    public required IReadOnlyDictionary<string, double> Signals { get; init; }

    /// <summary>Short explanation of why this memory matched.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// RAW topical relevance (similarity + keyword overlap + project match), unweighted — the
    /// same quantity the RelevanceFloor gates on. Distinct from <see cref="Score"/>, which
    /// folds in recency/importance/confidence and therefore cannot say whether a memory is
    /// ABOUT the query: in a live run, the user's dog memory scored 1.60 against a question
    /// about a carburetor. This is the candidate signal for "does Ava have relevant evidence
    /// for this turn" — being characterized from captured distributions before anything is
    /// allowed to threshold on it (docs/LANGUAGE_ORGAN.md, Phase 2 findings).
    /// </summary>
    public double Topical { get; init; }

    public RetrievalSource Source { get; init; } = RetrievalSource.Direct;
}
