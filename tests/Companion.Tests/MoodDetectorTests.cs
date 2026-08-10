using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Reading a message's emotional tone deterministically: fire on real cues, stay quiet on flat
/// text, and handle negation/intensifiers so "not great" and "really stressed" read correctly.
/// </summary>
public class MoodDetectorTests
{
    [Theory]
    [InlineData("I'm so stressed about work right now", Sentiment.VeryNegative)]
    [InlineData("feeling a bit tired today", Sentiment.Negative)]
    [InlineData("honestly I'm doing great", Sentiment.Positive)]
    [InlineData("I'm absolutely thrilled about the news", Sentiment.VeryPositive)]
    public void Detect_BucketsTheDominantEmotion(string message, Sentiment expected)
        => Assert.Equal(expected, MoodDetector.Detect(message).Sentiment);

    [Theory]
    [InlineData("Can you help me refactor this method?")]
    [InlineData("What time is the meeting?")]
    [InlineData("The build finished and the tests ran.")]
    [InlineData("")]
    [InlineData(null)]
    public void Detect_StaysNeutralOnFlatText(string? message)
        => Assert.True(MoodDetector.Detect(message).IsNeutral);

    [Fact]
    public void Detect_HandlesNegation_FlippingTheSign()
    {
        var notGreat = MoodDetector.Detect("today was not great honestly");
        Assert.True(notGreat.Valence < 0);
        Assert.Equal("not great", notGreat.Evidence);
        Assert.Null(notGreat.Label); // a "not great" user isn't labeled "great"

        var notStressed = MoodDetector.Detect("I'm not stressed about it anymore");
        Assert.True(notStressed.Valence > 0); // reassurance reads mildly positive
    }

    [Fact]
    public void Detect_Intensifiers_StrengthenTheReading()
    {
        var plain = MoodDetector.Detect("I'm stressed");
        var strong = MoodDetector.Detect("I'm really stressed");
        Assert.True(strong.Valence < plain.Valence); // more negative
        Assert.Equal(Sentiment.VeryNegative, strong.Sentiment);
    }

    [Fact]
    public void Detect_PicksTheStrongestCue_WhenSeveralAppear()
    {
        // "okay" (mild +) vs "devastated" (strong -) → the strong one drives it.
        var reading = MoodDetector.Detect("the plan is okay but I'm devastated about the layoffs");
        Assert.Equal(Sentiment.VeryNegative, reading.Sentiment);
        Assert.Equal("devastated", reading.Label);
    }
}
