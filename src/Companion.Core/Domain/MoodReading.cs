namespace Companion.Core.Domain;

/// <summary>
/// The result of reading one message's emotional tone: a valence bucket, a signed intensity, and —
/// when a specific cue fired — the dominant emotion word and the phrase that triggered it. Pure
/// value; the detector that produces it does no I/O.
/// </summary>
public readonly record struct MoodReading(Sentiment Sentiment, double Valence, string? Label, string? Evidence)
{
    /// <summary>A reading with no discernible emotion — the default for ordinary, flat messages.</summary>
    public static readonly MoodReading Neutral = new(Sentiment.Neutral, 0.0, null, null);

    /// <summary>True when nothing emotional was detected (no cue fired and the valence is flat).</summary>
    public bool IsNeutral => Sentiment == Sentiment.Neutral && Label is null;
}
