using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The repetition guard that stops a looping continuation: it recognises when a new chunk is just
/// text the reply already contains, while letting genuinely new continuation through.
/// </summary>
public class TextRepetitionTests
{
    [Fact]
    public void IdenticalParagraph_IsContained()
    {
        const string para = "I'm here to listen and offer any assistance that I can provide today.";
        Assert.True(TextRepetition.IsLargelyContained(para, "Sure! " + para));
    }

    [Fact]
    public void EmptyOrWhitespace_CountsAsRepeat()
    {
        Assert.True(TextRepetition.IsLargelyContained("   ", "anything at all here"));
    }

    [Fact]
    public void GenuinelyNewContinuation_IsNotContained()
    {
        var existing = "Once upon a time there was a small buoy bobbing in the harbor at dawn.";
        var next = "The next morning a storm rolled in and the crew scrambled to secure the deck.";
        Assert.False(TextRepetition.IsLargelyContained(next, existing));
    }

    [Fact]
    public void ShortDistinctChunk_IsNotContained()
    {
        Assert.False(TextRepetition.IsLargelyContained("chapter two", "chapter one"));
    }

    // ---- TrimLeadingOverlap: the "re-says the tail before continuing" echo ----

    [Fact]
    public void TrimLeadingOverlap_DropsTheEchoedTail()
    {
        var existing = "Night fell over the harbor and the crew";
        var continuation = "harbor and the crew lowered the sails.";
        Assert.Equal(" lowered the sails.", TextRepetition.TrimLeadingOverlap(existing, continuation));
    }

    [Fact]
    public void TrimLeadingOverlap_FullRestart_KeepsOnlyTheNewEnding()
    {
        var existing = "Once upon a time the buoy bobbed in the harbor.";
        var continuation = existing + " Then the storm came.";
        Assert.Equal(" Then the storm came.", TextRepetition.TrimLeadingOverlap(existing, continuation));
    }

    [Fact]
    public void TrimLeadingOverlap_GenuinelyNewText_IsUntouched()
    {
        var existing = "Chapter one ended right here.";
        var continuation = " Chapter two begins with a storm.";
        Assert.Equal(continuation, TextRepetition.TrimLeadingOverlap(existing, continuation));
    }

    [Fact]
    public void TrimLeadingOverlap_ShortAccidentalOverlap_IsNotTrimmed()
    {
        // "the" alone is far below the minimum — trimming it would corrupt real text.
        Assert.Equal("the end is near.", TextRepetition.TrimLeadingOverlap("Here comes the", "the end is near."));
    }

    [Fact]
    public void TrimLeadingOverlap_NeverSplitsAWord()
    {
        // A coincidental mid-word match ("…the harbor was" / "the harbor wasps…") must not be
        // trimmed — the seam would corrupt the text ("ps flew off").
        var existing = "He said the harbor was";
        var continuation = "the harbor wasps flew off.";
        Assert.Equal(continuation, TextRepetition.TrimLeadingOverlap(existing, continuation));
    }
}
