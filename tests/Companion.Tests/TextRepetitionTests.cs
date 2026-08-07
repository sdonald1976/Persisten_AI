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
}
