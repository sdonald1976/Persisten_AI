namespace Companion.Core.Domain;

/// <summary>
/// Provenance: links a memory back to the exact message that produced it, so the
/// companion can always answer "why do you believe this?" and so nothing is a free-floating
/// assertion. A memory with no evidence must not be accepted (enforced from Phase 3).
/// </summary>
public class MemoryEvidence
{
    public Guid Id { get; set; }

    /// <summary>
    /// The owning user. Carried explicitly so evidence reads can enforce ownership at the query
    /// level (WHERE MemoryId = ? AND UserId = ?) instead of trusting possession of a memory id.
    /// </summary>
    public string UserId { get; set; } = default!;

    /// <summary>The memory this evidence supports.</summary>
    public Guid MemoryId { get; set; }

    /// <summary>Which memory kind <see cref="MemoryId"/> refers to.</summary>
    public MemoryKind MemoryKind { get; set; }

    /// <summary>The source message.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The specific text excerpt cited.</summary>
    public string Excerpt { get; set; } = default!;

    /// <summary>0..1 — how strongly this excerpt supports the memory.</summary>
    public double Weight { get; set; } = 1.0;
}
