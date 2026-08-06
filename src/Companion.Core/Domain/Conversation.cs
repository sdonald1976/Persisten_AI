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

    /// <summary>
    /// Privacy: when true, this conversation does not create durable derived memory — memory
    /// extraction and project/open-loop updates are skipped for its turns. Raw messages are still
    /// stored (the turn needs them for in-session context); nothing is baked into long-term memory.
    /// Toggled by a spoken/typed "don't remember this conversation".
    /// </summary>
    public bool DoNotRemember { get; set; }
}
