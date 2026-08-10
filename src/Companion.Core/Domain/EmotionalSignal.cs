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
}
