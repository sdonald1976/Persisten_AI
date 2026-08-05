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
}
