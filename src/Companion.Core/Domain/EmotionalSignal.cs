namespace Companion.Core.Domain;

/// <summary>
/// One reading of how the user seemed to feel in a single message — the raw material of the
/// relational/emotional memory layer. This is an append-only log: the companion never rewrites how
/// a moment felt, it only accumulates readings, and the evolving "state of the relationship" is
/// always <em>derived</em> from this log (see <c>RelationshipSnapshot</c>), never a mutable blob
/// that could drift out of step with what actually happened.
/// </summary>
public sealed class EmotionalSignal
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The user message this reading was taken from (soft reference for explainability).</summary>
    public Guid MessageId { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The coarse valence bucket.</summary>
    public Sentiment Sentiment { get; set; }

    /// <summary>Signed intensity in [-1, 1]: negative = distressed, positive = upbeat, 0 = neutral.</summary>
    public double Valence { get; set; }

    /// <summary>Dominant emotion word, e.g. "stressed" or "excited"; null when only overall valence is known.</summary>
    public string? Label { get; set; }

    /// <summary>The cue phrase that triggered the reading, kept so a reading can explain itself.</summary>
    public string? Evidence { get; set; }

    /// <summary>
    /// What the feeling was <em>about</em> — a short subject phrase pulled from the same message
    /// ("the interview", "the deadline") or the project the turn resolved to. Null when the mood
    /// wasn't clearly tied to anything. This is what lets the companion follow up specifically:
    /// "how'd the interview go — you seemed nervous about it?"
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>The first-class project this feeling attached to, when the turn resolved to one.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// True once this concern has been closed out — either the companion has surfaced a follow-up
    /// about its topic (asked once, won't nag again) or a newer feeling about the same topic has
    /// superseded it. Followed-up signals stay in the history but no longer surface as an open
    /// concern to raise, so a worry the user has already engaged with isn't brought up again.
    /// </summary>
    public bool FollowedUp { get; set; }
}
