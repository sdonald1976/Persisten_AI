using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Computes the relationship's emotional snapshot from the recent signal log — average valence, the
/// current mood, a dominant recent emotion, and a coarse trend (is the mood rising or slipping?).
/// Everything is derived, nothing is stored, so the snapshot is always exactly what the log says.
/// </summary>
public sealed class RelationshipTracker : IRelationshipTracker
{
    // How many recent readings define "lately". Enough to smooth a single off day, short enough to
    // stay current.
    private const int Window = 10;

    // The most recent slice that defines the "right now" mood.
    private const int RecentSlice = 3;

    // A half-vs-half average must move at least this much to count as a real trend, not jitter.
    private const double TrendThreshold = 0.25;

    private readonly IEmotionStore _emotions;

    public RelationshipTracker(IEmotionStore emotions) => _emotions = emotions;

    public async Task<RelationshipSnapshot> BuildAsync(string userId, CancellationToken ct = default)
    {
        // Newest-first from the store; reverse to chronological for trend math.
        var recent = (await _emotions.GetRecentSignalsAsync(userId, Window, ct))
            .OrderBy(s => s.Timestamp)
            .ToList();
        if (recent.Count == 0)
            return RelationshipSnapshot.None;

        // Longer arc includes everything, even closed-out concerns — the trend can still show a low
        // that has since been resolved lifting back up.
        var average = recent.Average(s => s.Valence);
        var trend = Trend(recent);

        // The "right now" read and any concern to surface come from ACTIVE signals only: a worry the
        // user has already engaged with (followed-up) no longer counts as current, so it can't drag
        // the present mood or be raised again.
        var activeRecent = recent.TakeLast(RecentSlice).Where(s => !s.FollowedUp).ToList();
        var recentAverage = activeRecent.Count > 0 ? activeRecent.Average(s => s.Valence) : 0.0;
        var recentMood = Bucket(recentAverage);

        // The dominant recent signal: the freshest active one whose sign matches the recent mood (so a
        // lone upbeat aside inside a low stretch doesn't mislabel the mood). Its label AND its topic
        // come from the same signal, so "seemed {emotion} about {topic}" refers to one real moment.
        var dominant = activeRecent
            .AsEnumerable()
            .Reverse()
            .FirstOrDefault(s => s.Label is not null && Math.Sign(s.Valence) == Math.Sign(recentAverage));

        // Prefer the topic from the dominant signal; fall back to the freshest active topic that
        // shares the mood's direction, so a labelled-but-topicless cue still gets its subject.
        var recentTopic = dominant?.Topic
            ?? activeRecent.AsEnumerable().Reverse()
                .FirstOrDefault(s => s.Topic is not null && Math.Sign(s.Valence) == Math.Sign(recentAverage))
                ?.Topic;

        return new RelationshipSnapshot
        {
            SignalCount = recent.Count,
            AverageValence = Math.Round(average, 3),
            RecentMood = recentMood,
            RecentEmotion = dominant?.Label,
            RecentTopic = recentTopic,
            Trend = trend,
        };
    }

    private static MoodTrend Trend(IReadOnlyList<EmotionalSignal> chronological)
    {
        if (chronological.Count < 4)
            return MoodTrend.Steady; // not enough history to call a direction

        var mid = chronological.Count / 2;
        var older = chronological.Take(mid).Average(s => s.Valence);
        var newer = chronological.Skip(mid).Average(s => s.Valence);
        var delta = newer - older;

        if (delta >= TrendThreshold)
            return MoodTrend.Improving;
        if (delta <= -TrendThreshold)
            return MoodTrend.Declining;
        return MoodTrend.Steady;
    }

    private static Sentiment Bucket(double valence) => valence switch
    {
        <= -0.66 => Sentiment.VeryNegative,
        <= -0.20 => Sentiment.Negative,
        < 0.20 => Sentiment.Neutral,
        < 0.66 => Sentiment.Positive,
        _ => Sentiment.VeryPositive,
    };
}
