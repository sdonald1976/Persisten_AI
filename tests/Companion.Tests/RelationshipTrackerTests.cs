using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The relationship snapshot is derived purely from the signal log: current mood, dominant recent
/// emotion, and a coarse rising/slipping trend — and it only speaks up (Describe) when something is
/// genuinely notable.
/// </summary>
public class RelationshipTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeEmotionStore : IEmotionStore
    {
        private readonly List<EmotionalSignal> _signals = new();
        public Task AddSignalAsync(EmotionalSignal signal, CancellationToken ct = default)
        {
            _signals.Add(signal);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EmotionalSignal>> GetRecentSignalsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EmotionalSignal>>(
                _signals.Where(s => s.UserId == userId)
                    .OrderByDescending(s => s.Timestamp)
                    .Take(count)
                    .ToList());
    }

    private static EmotionalSignal Signal(string user, int minute, Sentiment s, double valence, string? label, string? topic = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = user,
            MessageId = Guid.NewGuid(),
            Timestamp = T0.AddMinutes(minute),
            Sentiment = s,
            Valence = valence,
            Label = label,
            Topic = topic,
        };

    private static async Task<(RelationshipTracker tracker, FakeEmotionStore store)> WithSignals(params EmotionalSignal[] signals)
    {
        var store = new FakeEmotionStore();
        foreach (var s in signals)
            await store.AddSignalAsync(s);
        return (new RelationshipTracker(store), store);
    }

    [Fact]
    public async Task NoSignals_YieldsAnEmptySnapshot_ThatSaysNothing()
    {
        var (tracker, _) = await WithSignals();
        var snap = await tracker.BuildAsync("u");
        Assert.False(snap.HasHistory);
        Assert.Null(snap.Describe());
    }

    [Fact]
    public async Task RecentNegativeMood_SurfacesTheEmotion_AndAskForGentleness()
    {
        var (tracker, _) = await WithSignals(
            Signal("u", 0, Sentiment.Negative, -0.6, "stressed"),
            Signal("u", 1, Sentiment.Negative, -0.55, "worried"),
            Signal("u", 2, Sentiment.VeryNegative, -0.8, "overwhelmed"));

        var snap = await tracker.BuildAsync("u");

        Assert.True(snap.RecentMood is Sentiment.Negative or Sentiment.VeryNegative);
        Assert.Equal("overwhelmed", snap.RecentEmotion); // freshest matching label
        Assert.Contains("gentler", snap.Describe()!);
    }

    [Fact]
    public async Task Trend_IsImproving_WhenTheNewerHalfIsBrighter()
    {
        var (tracker, _) = await WithSignals(
            Signal("u", 0, Sentiment.VeryNegative, -0.8, "miserable"),
            Signal("u", 1, Sentiment.Negative, -0.6, "sad"),
            Signal("u", 2, Sentiment.Positive, 0.4, "better"),
            Signal("u", 3, Sentiment.Positive, 0.6, "glad"));

        var snap = await tracker.BuildAsync("u");

        Assert.Equal(MoodTrend.Improving, snap.Trend);
    }

    [Fact]
    public async Task PositiveMood_ShareTheEnergy()
    {
        var (tracker, _) = await WithSignals(
            Signal("u", 0, Sentiment.Positive, 0.6, "excited"),
            Signal("u", 1, Sentiment.VeryPositive, 0.85, "thrilled"));

        var snap = await tracker.BuildAsync("u");

        Assert.True(snap.RecentMood is Sentiment.Positive or Sentiment.VeryPositive);
        Assert.Contains("share in that", snap.Describe()!);
    }

    [Fact]
    public async Task RecentTopic_CarriesTheSubjectOfTheDominantFeeling()
    {
        var (tracker, _) = await WithSignals(
            Signal("u", 0, Sentiment.Negative, -0.5, "worried", topic: "the move"),
            Signal("u", 1, Sentiment.Negative, -0.6, "nervous", topic: "the interview"));

        var snap = await tracker.BuildAsync("u");

        Assert.Equal("nervous", snap.RecentEmotion);   // freshest matching signal
        Assert.Equal("the interview", snap.RecentTopic); // …and its subject
        Assert.Contains("the interview", snap.Describe()!);
    }

    [Fact]
    public async Task Snapshot_IsScopedToTheUser()
    {
        var (tracker, _) = await WithSignals(
            Signal("mine", 0, Sentiment.VeryNegative, -0.9, "devastated"),
            Signal("theirs", 1, Sentiment.VeryPositive, 0.9, "thrilled"));

        var snap = await tracker.BuildAsync("mine");

        Assert.Equal(1, snap.SignalCount);
        Assert.True(snap.AverageValence < 0);
    }
}
