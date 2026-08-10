using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>Human-friendly elapsed-time phrasing for time-aware greetings ("it's been 3 days").</summary>
public class RelativeTimeTests
{
    [Theory]
    [InlineData(0, "a moment")]
    [InlineData(3, "a few minutes")]
    [InlineData(20, "20 minutes")]
    [InlineData(90, "about an hour")]
    [InlineData(60 * 5, "5 hours")]
    [InlineData(60 * 30, "a day")]        // 30 hours
    [InlineData(60 * 24 * 3, "3 days")]
    [InlineData(60 * 24 * 8, "a week")]
    [InlineData(60 * 24 * 20, "3 weeks")]
    [InlineData(60 * 24 * 60, "2 months")]
    [InlineData(60 * 24 * 800, "2 years")]
    public void Describe_ReadsNaturally(int minutes, string expected)
        => Assert.Equal(expected, RelativeTime.Describe(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void NegativeSpan_IsClampedToAMoment()
        => Assert.Equal("a moment", RelativeTime.Describe(TimeSpan.FromMinutes(-5)));
}
