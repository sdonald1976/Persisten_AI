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

    // ---- special-token spans (<|…|>) ----
    //
    // A conversational fine-tune shown a tool-call protocol will sometimes imitate its shape in
    // prose. These are verbatim from a real Stheno reply: the protocol leaked into what the user
    // saw. Llama-family models also use this delimiter for their own control tokens.

    [Fact]
    public void StripAll_RemovesALeakedToolProtocolSpan()
        => Assert.Equal(
            "Based on our previous conversations, you mentioned preferring your coffee as dark roast.",
            ReasoningFilter.StripAll(
                "Based on our previous conversations, you mentioned preferring your coffee as dark roast."
                + " <|\"memory.confirmed\".code=\"ok\", \"data\":{\"confidence\":0.95}|>"));

    [Fact]
    public void StripAll_RemovesAControlTokenMidSentence()
        => Assert.Equal("Good evening. How are you?",
            ReasoningFilter.StripAll("Good evening.<|eot_id|> How are you?"));

    [Fact]
    public void StripAll_UnclosedSpecialSpan_IsDropped()
        => Assert.Equal("All done.", ReasoningFilter.StripAll("All done.<|never closed"));

    [Fact]
    public void StripAll_LeavesAnOrdinaryPipeAlone()
        => Assert.Equal("Use a | b for alternation, and a < b for less-than.",
            ReasoningFilter.StripAll("Use a | b for alternation, and a < b for less-than."));

    [Fact]
    public void StripAll_HandlesBothKindsTogether()
        => Assert.Equal("Hello there!",
            ReasoningFilter.StripAll("<think>be warm</think>Hello there!<|eot_id|>"));

    [Fact]
    public void Streaming_SplitSpecialSpanAcrossChunks_IsStillStripped()
    {
        var filter = new ReasoningFilter();
        // Both the opener and the closer are split mid-delimiter, which is the case a naive
        // post-hoc regex on each chunk would miss entirely.
        var chunks = new[] { "The answer.", "<", "|tool.call code=\"ok\"", "|", "> Anything else?" };

        var visible = string.Concat(chunks.Select(filter.Feed)) + filter.Flush();

        Assert.Equal("The answer. Anything else?", visible);
    }

    [Fact]
    public void Streaming_LoneAngleThenPipe_DoesNotSwallowTheReply()
    {
        // "<" arriving alone must be held (it could start either opener) but then released once
        // the next chunk proves it was just punctuation.
        var filter = new ReasoningFilter();
        var chunks = new[] { "If a <", " b then stop." };

        var visible = string.Concat(chunks.Select(filter.Feed)) + filter.Flush();

        Assert.Equal("If a < b then stop.", visible);
    }
}
