namespace Companion.Core.Domain;

/// <summary>
/// A derived, read-only view of how the relationship has been feeling lately, computed on demand
/// from the append-only <see cref="EmotionalSignal"/> log — never stored, so it can never drift.
/// It exists to let the companion attune its <em>tone</em> (be gentler when the user has been low,
/// share in good spirits when they've been up), not to assert facts about the user.
/// </summary>
public sealed record RelationshipSnapshot
{
    /// <summary>An empty snapshot — no emotional history to draw on yet.</summary>
    public static readonly RelationshipSnapshot None = new();

    /// <summary>How many recent signals this snapshot summarizes.</summary>
    public int SignalCount { get; init; }

    /// <summary>Whether there is any emotional history at all.</summary>
    public bool HasHistory => SignalCount > 0;

    /// <summary>Rolling average valence across the recent window, in [-1, 1].</summary>
    public double AverageValence { get; init; }

    /// <summary>The mood of the most recent interactions (the "right now" read).</summary>
    public Sentiment RecentMood { get; init; } = Sentiment.Neutral;

    /// <summary>Which way the mood has been trending across the window.</summary>
    public MoodTrend Trend { get; init; } = MoodTrend.Steady;

    /// <summary>The dominant recent emotion word ("stressed", "excited"), when one stands out.</summary>
    public string? RecentEmotion { get; init; }

    /// <summary>
    /// What that recent feeling was about ("the interview"), when the message said — so the
    /// companion can follow up specifically rather than just noting a general mood.
    /// </summary>
    public string? RecentTopic { get; init; }

    /// <summary>
    /// A single, careful line of tone guidance for the model — or null when nothing is notable
    /// enough to mention. Phrased as an observation to hold loosely, not a fact to state back.
    /// </summary>
    public string? Describe()
    {
        if (!HasHistory)
            return null;

        var emotion = string.IsNullOrWhiteSpace(RecentEmotion) ? null : RecentEmotion;
        var topic = string.IsNullOrWhiteSpace(RecentTopic) ? null : RecentTopic;

        // A clearly low recent mood is the case that matters most — lead with care.
        if (RecentMood is Sentiment.Negative or Sentiment.VeryNegative)
        {
            // Tied to a specific topic, it's fine (kind, even) to gently check in on that thing.
            if (emotion is not null && topic is not null)
                return $"The user has seemed {emotion} about {topic} lately — it's fine to gently ask how that's going.";

            var lately = emotion is null ? "has seemed low lately" : $"has seemed {emotion} lately";
            return $"The user {lately}. Be a little gentler and more attentive; don't bring this up unless they do.";
        }

        // Coming up from a rough patch is worth meeting warmly.
        if (Trend == MoodTrend.Improving && AverageValence < 0.2)
            return "The user seems to be coming out of a rough patch. Meet any good news warmly.";

        // Slipping even while still net-positive — stay attuned.
        if (Trend == MoodTrend.Declining && RecentMood != Sentiment.Neutral)
            return "The user's mood seems to have dipped from where it was. Stay attuned to how they're doing.";

        if (RecentMood is Sentiment.Positive or Sentiment.VeryPositive)
        {
            var upbeat = emotion is null ? "has been in good spirits lately" : $"has seemed {emotion} lately";
            return $"The user {upbeat}. You can share in that energy.";
        }

        return null;
    }
}
