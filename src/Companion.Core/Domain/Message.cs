namespace Companion.Core.Domain;

/// <summary>
/// A single message in a conversation. Stored durably so history survives beyond the
/// model's context window, and so memories can cite the exact messages that produced them.
/// </summary>
public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string UserId { get; set; } = default!;

    public MessageRole Role { get; set; }
    public string Content { get; set; } = default!;

    /// <summary>Reply relationship, when this message answers another.</summary>
    public Guid? ReplyToId { get; set; }

    /// <summary>Approximate token/length metadata for budgeting.</summary>
    public int? TokenCount { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
