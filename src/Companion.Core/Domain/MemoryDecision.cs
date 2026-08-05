namespace Companion.Core.Domain;

/// <summary>
/// The pipeline's verdict on one candidate, with the reasoning. Surfaced in diagnostics so
/// it's clear which candidates were extracted and which were accepted, merged, rejected, or
/// deferred — and why.
/// </summary>
public sealed record MemoryDecision
{
    public required MemoryCandidate Candidate { get; init; }
    public required MemoryDecisionKind Outcome { get; init; }
    public required string Reason { get; init; }

    /// <summary>The final computed confidence (may differ from the candidate's proposed value).</summary>
    public double FinalConfidence { get; init; }

    /// <summary>The memory created (Accepted/NeedsReview) or updated (Merged), when applicable.</summary>
    public Guid? ResultingMemoryId { get; init; }

    /// <summary>The existing memory a Merged/duplicate candidate matched against.</summary>
    public Guid? MatchedMemoryId { get; init; }
}
