namespace Companion.Core.Domain;

/// <summary>A conversation thread. Messages hang off it via <see cref="Message.ConversationId"/>.</summary>
public class Conversation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public string? Title { get; set; }

    /// <summary>The chat model used for this conversation (provenance).</summary>
    public string? ModelUsed { get; set; }

    /// <summary>Where the conversation came from (e.g. "cli", "import").</summary>
    public string? Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}
