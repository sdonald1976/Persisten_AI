namespace Companion.Core.Domain;

/// <summary>
/// An unfinished matter: a task, an unanswered question, a promised follow-up, an experiment
/// awaiting results, a purchase awaiting arrival, a deployment awaiting testing, or a topic
/// intentionally postponed. The companion recalls these when contextually relevant rather than
/// nagging about them.
/// </summary>
public class OpenLoop
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid? ProjectId { get; set; }

    public string Description { get; set; } = default!;
    public string Owner { get; set; } = "user";

    public OpenLoopStatus Status { get; set; } = OpenLoopStatus.Open;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpectedFollowUp { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The message that opened the loop (provenance).</summary>
    public Guid? SourceMessageId { get; set; }

    /// <summary>Evidence that closed it, when resolved.</summary>
    public string? ClosureEvidence { get; set; }

    /// <summary>Embedding of <see cref="Description"/> for relevance matching.</summary>
    public float[]? Embedding { get; set; }
}
