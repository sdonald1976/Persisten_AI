using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Pulling out what a feeling is about, so a mood can be followed up on. Conservative: only when the
/// message says what it's about, and never a contentless "it"/"everything".
/// </summary>
public class MoodTopicTests
{
    [Theory]
    [InlineData("I'm really stressed about the deadline", "the deadline")]
    [InlineData("honestly nervous about my interview on Thursday", "my interview")]
    [InlineData("I've been worried about the buoy project lately", "the buoy project")]
    [InlineData("so excited for the trip next week", "the trip")]
    [InlineData("feeling anxious over the presentation tomorrow", "the presentation")]
    public void Extract_FindsTheSubjectOfTheFeeling(string message, string expected)
        => Assert.Equal(expected, MoodTopic.Extract(message));

    [Theory]
    [InlineData("I'm so stressed about it")]          // contentless object
    [InlineData("just feeling overwhelmed by everything")]  // contentless + wrong preposition
    [InlineData("I'm really happy today")]            // no "about" phrase at all
    [InlineData("that was great, thanks!")]
    [InlineData("")]
    [InlineData(null)]
    public void Extract_ReturnsNull_WhenThereIsNoFollowableSubject(string? message)
        => Assert.Null(MoodTopic.Extract(message));
}
