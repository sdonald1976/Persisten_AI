namespace Companion.Core.Domain;

/// <summary>One entry in a project's chronological activity log.</summary>
public class ProjectEvent
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string UserId { get; set; } = default!;

    public ProjectEventKind Kind { get; set; }
    public string Description { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The message that prompted this entry, when applicable (provenance).</summary>
    public Guid? SourceMessageId { get; set; }
}
