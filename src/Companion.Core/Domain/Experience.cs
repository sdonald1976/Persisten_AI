namespace Companion.Core.Domain;

/// <summary>
/// Something that happened to her, in her own life rather than the user's.
///
/// This is the companion's record of her own experience — not a model of the world it came from.
/// The distinction is the design's central rule and it is visible in the shape: there is no place
/// table here, no occupancy, no layout, nothing to keep in step with anywhere. Just a timestamped
/// sentence about a thing that happened, which is what reflection can read and what she can later
/// think about.
///
/// It is emphatically NOT a memory. Nothing here is a fact about the user's real life, and nothing
/// here reaches the fact store — an afternoon in the greenhouse says nothing about them at all.
/// </summary>
public sealed class Experience
{
    public required Guid Id { get; init; }

    public required string UserId { get; init; }

    /// <summary>When it happened, as reported by wherever it happened.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Where it came from — "world" today. Kept so a second source stays distinguishable.</summary>
    public required string Source { get; init; }

    /// <summary>What sort of thing it was ("arrived", "presence"). The source's own vocabulary.</summary>
    public required string Kind { get; init; }

    /// <summary>A plain sentence: "You went to the greenhouse." Written by whoever reported it.</summary>
    public required string Text { get; init; }
}
