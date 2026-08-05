namespace Companion.Core.Domain;

/// <summary>
/// A durable fact or preference (subject-predicate-value), with confidence, validity,
/// and supersession history. Not every statement is permanently true: temporary states
/// are marked <see cref="Validity.Temporary"/> and never promoted to lifelong facts.
/// </summary>
public class SemanticMemory : IMemory
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>Normalized subject, e.g. "user".</summary>
    public string Subject { get; set; } = default!;

    /// <summary>Predicate/category, e.g. "prefers", "uses", "diet".</summary>
    public string Predicate { get; set; } = default!;

    /// <summary>The value, e.g. "step-by-step cooking instructions".</summary>
    public string Value { get; set; } = default!;

    /// <summary>Human-readable normalized fact used for embedding/keyword matching.</summary>
    public string NormalizedFact { get; set; } = default!;

    public double Confidence { get; set; }
    public double Importance { get; set; }

    public Validity Validity { get; set; } = Validity.Current;
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    /// <summary>Whether the user stated this, the system inferred it, or it was consolidated.</summary>
    public MemoryOrigin Origin { get; set; } = MemoryOrigin.Stated;

    public DateTimeOffset FirstObserved { get; set; }
    public DateTimeOffset LastConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when this fact has been replaced; points to the replacement.</summary>
    public Guid? SupersededById { get; set; }

    public string? RelatedProject { get; set; }

    public float[]? Embedding { get; set; }

    public ICollection<MemoryEvidence> Evidence { get; set; } = new List<MemoryEvidence>();

    // --- IMemory projection ---
    MemoryKind IMemory.Kind => MemoryKind.Semantic;
    string IMemory.Content => NormalizedFact;
    DateTimeOffset IMemory.EffectiveAt => LastConfirmed;
}
