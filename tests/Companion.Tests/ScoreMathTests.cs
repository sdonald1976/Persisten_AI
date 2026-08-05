using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

public class ScoreMathTests
{
    [Fact]
    public void Cosine_IdenticalVectors_IsOne()
    {
        var v = new[] { 1f, 2f, 3f };
        Assert.Equal(1.0, ScoreMath.Cosine(v, v), precision: 5);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero()
    {
        Assert.Equal(0.0, ScoreMath.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }), precision: 5);
    }

    [Fact]
    public void Cosine_MismatchedOrEmpty_IsZero()
    {
        Assert.Equal(0.0, ScoreMath.Cosine(new[] { 1f, 2f }, new[] { 1f }));
        Assert.Equal(0.0, ScoreMath.Cosine(Array.Empty<float>(), Array.Empty<float>()));
        Assert.Equal(0.0, ScoreMath.Cosine(null, new[] { 1f }));
    }

    [Fact]
    public void Recency_AtZeroAge_IsOne_AndHalvesEachHalfLife()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1.0, ScoreMath.Recency(now, now, halfLifeDays: 30), precision: 5);
        Assert.Equal(0.5, ScoreMath.Recency(now.AddDays(-30), now, halfLifeDays: 30), precision: 5);
        Assert.Equal(0.25, ScoreMath.Recency(now.AddDays(-60), now, halfLifeDays: 30), precision: 5);
    }

    [Fact]
    public void Recency_FutureTimestamp_ClampsToOne()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1.0, ScoreMath.Recency(now.AddDays(5), now, halfLifeDays: 30), precision: 5);
    }

    [Fact]
    public void KeywordOverlap_SharesStemmedTokens()
    {
        // "tested" and "test" collapse via the stemmer, so these overlap.
        Assert.True(ScoreMath.KeywordOverlap("I finally tested it", "planned to test the board") > 0);
        Assert.Equal(0.0, ScoreMath.KeywordOverlap("apples oranges", "quantum entanglement"));
    }
}
