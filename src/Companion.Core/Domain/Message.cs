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

    // ---- Generation metadata (assistant messages only; null for user messages) ----
    // Recorded so "why did it stop / how was this produced" is answerable after the fact,
    // not just guessable. See ChatCompletion.

    /// <summary>Provider stop reason for the final round: "stop", "length", … . Null for user messages.</summary>
    public string? FinishReason { get; set; }

    /// <summary>Generation rounds it took (1 = single call; &gt;1 = auto-continued).</summary>
    public int? GenerationRounds { get; set; }

    /// <summary>True if the reply was still cut off by the token limit when generation stopped.</summary>
    public bool? Truncated { get; set; }

    /// <summary>The model that produced this reply, as the server reported it (or the configured name).</summary>
    public string? ModelUsed { get; set; }

    /// <summary>Prompt/input tokens billed for this reply, if the server reported usage.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion/output tokens billed for this reply, if the server reported usage.</summary>
    public int? CompletionTokens { get; set; }
}
