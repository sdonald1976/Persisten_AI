namespace Companion.Core.Domain;

/// <summary>
/// A user's rating of a specific reply, captured for later style fine-tuning. Positive replies
/// become SFT targets; a negative reply paired with a later good one becomes a DPO pair. Kept as
/// data, never auto-applied to the model.
/// </summary>
public class FeedbackRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid ConversationId { get; set; }

    public string UserMessage { get; set; } = default!;
    public string Response { get; set; } = default!;

    public FeedbackRating Rating { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
