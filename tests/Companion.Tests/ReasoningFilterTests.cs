using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Stripping the model's &lt;think&gt; reasoning trace from its output — so it's never shown, stored,
/// or fed back into the next turn (which was pushing the model into repeating itself). Must work on a
/// whole string and on a token stream where tags are split across chunks.
/// </summary>
public class ReasoningFilterTests
{
    [Fact]
    public void StripAll_RemovesThinkBlock_KeepsTheReply()
        => Assert.Equal("Hello there!",
            ReasoningFilter.StripAll("<think>the user greeted me, be friendly</think>Hello there!"));

    [Fact]
    public void StripAll_HandlesThinkingVariant_AndSurroundingText()
        => Assert.Equal("The answer is 42.",
            ReasoningFilter.StripAll("<thinking>lots of reasoning</thinking>The answer is 42."));

    [Fact]
    public void StripAll_NoThink_IsUnchanged()
        => Assert.Equal("Just a normal reply.", ReasoningFilter.StripAll("Just a normal reply."));

    [Fact]
    public void StripAll_UnclosedThink_IsDropped()
        => Assert.Equal("", ReasoningFilter.StripAll("<think>reasoning that never closed"));

    [Fact]
    public void StripAll_DoesNotEatLegitimateAngleBrackets()
        => Assert.Equal("Use 3 < 5 and a > b in your check.",
            ReasoningFilter.StripAll("Use 3 < 5 and a > b in your check."));

    [Fact]
    public void Streaming_SplitTagsAcrossChunks_AreStillStripped()
    {
        var filter = new ReasoningFilter();
        // The <think>…</think> tags are deliberately split mid-tag between chunks.
        var chunks = new[] { "<thi", "nk>secret rea", "soning</thi", "nk>Here", " is the reply." };

        var visible = string.Concat(chunks.Select(filter.Feed)) + filter.Flush();

        Assert.Equal("Here is the reply.", visible);
    }

    [Fact]
    public void Streaming_NoThink_PassesEverythingThrough()
    {
        var filter = new ReasoningFilter();
        var chunks = new[] { "Hel", "lo ", "world." };

        var visible = string.Concat(chunks.Select(filter.Feed)) + filter.Flush();

        Assert.Equal("Hello world.", visible);
    }
}
