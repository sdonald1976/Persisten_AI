namespace Companion.Core.Domain;

/// <summary>
/// An immutable audit-trail entry for a memory. Every acceptance, confirmation, merge (and
/// later supersession/deletion) writes one, so the history of how a memory came to be — and
/// changed — is always inspectable. This is what backs "why do you believe this?".
/// </summary>
public class MemoryRevision
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public MemoryKind MemoryKind { get; set; }

    public RevisionKind Kind { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Who/what made the change, e.g. "extraction-pipeline" or "user".</summary>
    public string Actor { get; set; } = "extraction-pipeline";

    /// <summary>Human-readable note describing the change.</summary>
    public string? Note { get; set; }

    /// <summary>Optional snapshot of relevant fields before the change (for updates/merges).</summary>
    public string? Before { get; set; }

    /// <summary>Optional snapshot of relevant fields after the change.</summary>
    public string? After { get; set; }
}
