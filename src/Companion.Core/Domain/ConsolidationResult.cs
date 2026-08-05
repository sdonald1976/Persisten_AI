namespace Companion.Core.Domain;

/// <summary>The outcome of a consolidation pass: which higher-level memories were created.</summary>
public sealed record ConsolidationResult
{
    public required IReadOnlyList<ConsolidatedGroup> Created { get; init; }

    public static readonly ConsolidationResult Empty = new() { Created = Array.Empty<ConsolidatedGroup>() };
}

/// <summary>One consolidated memory and the low-level memories it was rolled up from.</summary>
public sealed record ConsolidatedGroup
{
    public required Guid ConsolidatedMemoryId { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<Guid> SourceMemoryIds { get; init; }
    public required int EvidenceCount { get; init; }
}
