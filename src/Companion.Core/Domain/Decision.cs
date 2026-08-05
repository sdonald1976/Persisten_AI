namespace Companion.Core.Domain;

/// <summary>
/// An important decision the user made, optionally tied to a project. Kept as its own record
/// so that when the user returns to a project the companion can recall what was decided and why
/// — and so a later reversal supersedes rather than erases it (Phase 5).
/// </summary>
public class Decision
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid? ProjectId { get; set; }

    public string Statement { get; set; } = default!;
    public string? Rationale { get; set; }
    public DateTimeOffset DecidedAt { get; set; }

    /// <summary>Active by default; set Superseded when reversed (Phase 5).</summary>
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;

    public Guid? SourceMessageId { get; set; }
}
