namespace Companion.Core.Domain;

/// <summary>The outcome of running the extraction pipeline over one exchange.</summary>
public sealed record MemoryExtractionResult
{
    public required IReadOnlyList<MemoryDecision> Decisions { get; init; }

    public int Accepted => Decisions.Count(d => d.Outcome == MemoryDecisionKind.Accepted);
    public int Merged => Decisions.Count(d => d.Outcome == MemoryDecisionKind.Merged);
    public int Rejected => Decisions.Count(d => d.Outcome == MemoryDecisionKind.Rejected);
    public int NeedsReview => Decisions.Count(d => d.Outcome == MemoryDecisionKind.NeedsReview);
    public int Superseded => Decisions.Count(d => d.Outcome == MemoryDecisionKind.Superseded);

    public static readonly MemoryExtractionResult Empty = new() { Decisions = Array.Empty<MemoryDecision>() };
}
