namespace Companion.Core.Domain;

/// <summary>
/// A message the companion sent on its own initiative (push notification), recorded so outreach
/// stays honest and rare: the log is what enforces the budget, and it's how she can later say
/// "I pinged you yesterday" and be right.
/// </summary>
public sealed class OutboundMessage
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The message text exactly as sent.</summary>
    public string Text { get; set; } = default!;

    /// <summary>What prompted it, e.g. "curiosity:{id}" — provenance, like everywhere else.</summary>
    public string Source { get; set; } = default!;

    public DateTimeOffset SentAt { get; set; }
}
