namespace Companion.Core.Domain;

/// <summary>
/// Something that happened. Distinguishes three times: when it happened
/// (<see cref="EventTime"/>), when the user mentioned it (<see cref="MentionedAt"/>),
/// and when the memory was recorded (<see cref="CreatedAt"/>).
/// A Planned/InProgress episode functions as an open loop for retrieval in Phase 2.
/// </summary>
public class EpisodicMemory : IMemory
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    public string Description { get; set; } = default!;

    public DateTimeOffset EventTime { get; set; }
    public TimePrecision TimePrecision { get; set; } = TimePrecision.Day;
    public DateTimeOffset MentionedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The episode's own status (open-loop-ness lives here, not in the lifecycle).</summary>
    public EpisodeStatus EpisodeStatus { get; set; } = EpisodeStatus.Occurred;

    public double Importance { get; set; }
    public double Confidence { get; set; }

    /// <summary>Only set when emotional significance is explicitly evident in the source.</summary>
    public string? EmotionalSignificance { get; set; }

    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    /// <summary>
    /// Whose story this is. <see cref="MemoryOwner.Shared"/> marks a moment the user and the
    /// companion had TOGETHER — shared history, presented as "remember when we…", never as a
    /// bare fact about the user.
    /// </summary>
    public MemoryOwner Owner { get; set; } = MemoryOwner.User;

    public string? RelatedProject { get; set; }

    public float[]? Embedding { get; set; }

    public ICollection<MemoryEvidence> Evidence { get; set; } = new List<MemoryEvidence>();

    /// <summary>True when this episode represents an unresolved matter to be recalled later.</summary>
    public bool IsOpenLoop => EpisodeStatus is EpisodeStatus.Planned or EpisodeStatus.InProgress;

    // --- IMemory projection ---
    MemoryKind IMemory.Kind => MemoryKind.Episodic;
    string IMemory.Content => Description;
    DateTimeOffset IMemory.EffectiveAt => MentionedAt;
}
