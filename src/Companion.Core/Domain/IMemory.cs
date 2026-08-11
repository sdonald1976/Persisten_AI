namespace Companion.Core.Domain;

/// <summary>
/// The common view the retrieval layer needs over any memory, regardless of kind.
/// Semantic and episodic memories both implement this so scoring can treat them uniformly
/// without inheritance. Kind-specific fields live on the concrete records.
/// </summary>
public interface IMemory
{
    Guid Id { get; }
    string UserId { get; }
    MemoryKind Kind { get; }

    /// <summary>Whose story this is: the user's, the companion's own, or a shared moment.</summary>
    MemoryOwner Owner { get; }

    /// <summary>Canonical text used for embedding and keyword matching.</summary>
    string Content { get; }

    /// <summary>0..1 — how much this memory should be favored when relevant.</summary>
    double Importance { get; }

    /// <summary>0..1 — how strongly we believe it.</summary>
    double Confidence { get; }

    MemoryStatus Status { get; }

    /// <summary>When the record was written.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The time that matters for recency scoring: when the fact was last confirmed
    /// (semantic) or when the event was mentioned (episodic).
    /// </summary>
    DateTimeOffset EffectiveAt { get; }

    /// <summary>Optional project this memory is associated with (name/alias).</summary>
    string? RelatedProject { get; }

    /// <summary>Cached embedding of <see cref="Content"/>. Derived; regenerable from the record.</summary>
    float[]? Embedding { get; set; }
}
