using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// She does not introduce herself, by name, at the start of every message.
///
/// The packet labels the transcript by speaker ("[COMPANION - Ava]"), and a model shown that shape
/// adopts it and opens with "Ava: ". Observed on every turn of a fresh conversation, and it
/// compounds: the prefixed reply is stored, fed back as the previous turn, and now demonstrates the
/// format directly beneath the label that inspired it.
///
/// The boundary these pin is the important part. Removing a label she wrote is a small win;
/// removing words she meant is a real loss, so anything that is not clearly her own name or a role
/// word is left exactly as she wrote it.
/// </summary>
public class SelfLabelTests
{
    [Theory]
    [InlineData("Ava: Ah, hello again!", "Ah, hello again!")]
    [InlineData("Ava:Ah, hello again!", "Ah, hello again!")]
    [InlineData("  Ava:   Ah, hello again!", "Ah, hello again!")]
    [InlineData("Ava:\nAh, hello again!", "Ah, hello again!")]
    [InlineData("ava: Ah, hello again!", "Ah, hello again!")]
    public void SheDoesNotSayHerOwnNameFirst(string reply, string expected)
        => Assert.Equal(expected, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));

    [Theory]
    [InlineData("Assistant: Sure, I can help.", "Sure, I can help.")]
    [InlineData("Companion: Sure, I can help.", "Sure, I can help.")]
    [InlineData("[COMPANION - Ava]\nSure, I can help.", "Sure, I can help.")]
    [InlineData("[COMPANION] Sure, I can help.", "Sure, I can help.")]
    public void TheGenericRoleLabelsGoToo(string reply, string expected)
        => Assert.Equal(expected, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));

    [Theory]
    [InlineData("Ava Hi again! Thanks for sharing.", "Hi again! Thanks for sharing.")]
    [InlineData("Ava — Hi again!", "Hi again!")]
    [InlineData("Ava\nHi again!", "Hi again!")]
    public void TheLabelWithItsPunctuationMissingGoesToo(string reply, string expected)
    {
        // Seen live after the colon form was fixed: a roleplay fine-tune trained on "Name:"
        // dialogue reaches for the name whether or not it remembers the colon.
        Assert.Equal(expected, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));
    }

    [Theory]
    [InlineData("Ava is what my mother chose, apparently.")]
    [InlineData("Avalanche season starts about now.")]
    [InlineData("Ava, I think you already know the answer.")]
    public void HerNameInAnActualSentenceIsLeftAlone(string reply)
    {
        // A lower-case word after the name means it is the subject of a sentence, not a label;
        // a comma means someone is being addressed. Both are hers.
        Assert.Equal(reply, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));
    }

    [Fact]
    public void ALabelRepeatedTwiceIsStillRemoved()
        => Assert.Equal("Hello.", PromptEchoFilter.TrimSelfLabel("[COMPANION - Ava] Ava: Hello.", "Ava"));

    [Fact]
    public void WithNoNameKnown_TheRoleWordsAreStillRemoved()
        => Assert.Equal("Sure.", PromptEchoFilter.TrimSelfLabel("Assistant: Sure.", speaker: null));

    // ---- what must never be touched ----

    [Theory]
    [InlineData("Note: the deck quote came in high.")]
    [InlineData("Update: I finished the reading.")]
    [InlineData("Scott: did you mean the other one?")]
    [InlineData("Honestly: I have no idea.")]
    [InlineData("Here's the thing: it rained all week.")]
    [InlineData("I was thinking about Biscuit today.")]
    public void WordsSheMeantAreLeftAlone(string reply)
        => Assert.Equal(reply, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));

    [Fact]
    public void AReplyThatIsOnlyALabelIsKept()
    {
        // Strange, but it is what she said, and returning nothing would be worse.
        Assert.Equal("Ava:", PromptEchoFilter.TrimSelfLabel("Ava:", "Ava"));
    }

    [Fact]
    public void AColonFarIntoASentenceIsNotALabel()
    {
        const string reply = "I kept turning it over all afternoon and eventually landed here: it needs replacing.";
        Assert.Equal(reply, PromptEchoFilter.TrimSelfLabel(reply, "Ava"));
    }

    // ---- the stream is what the user actually sees ----

    [Fact]
    public void TheLabelNeverReachesTheStream()
    {
        // Cleaning the finished text is not enough: the client keeps the streamed tokens and
        // discards the cleaned reply, and speech is synthesized from them as they arrive.
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        foreach (var chunk in new[] { "Ava", ":", " Ah, hello", " again! How", " have you been?" })
            sink.Report(chunk);
        sink.Flush();

        Assert.Equal("Ah, hello again! How have you been?", string.Concat(seen));
    }

    [Fact]
    public void AShortReplyStillArrives()
    {
        // Below the sink's window nothing triggers a flush on its own, so the round end must.
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        sink.Report("Ava: Morning.");
        sink.Flush();

        Assert.Equal("Morning.", string.Concat(seen));
    }

    [Fact]
    public void AnUnlabelledStreamPassesThroughUnchanged()
    {
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        foreach (var chunk in new[] { "That deck", " sounds like", " a lot of work." })
            sink.Report(chunk);
        sink.Flush();

        Assert.Equal("That deck sounds like a lot of work.", string.Concat(seen));
    }

    [Fact]
    public void FlushingTwiceEmitsNothingExtra()
    {
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        sink.Report("Ava: Morning.");
        sink.Flush();
        sink.Flush();

        Assert.Equal("Morning.", string.Concat(seen));
    }

    private sealed class Collector : IProgress<string>
    {
        private readonly List<string> _seen;
        public Collector(List<string> seen) => _seen = seen;
        public void Report(string value) => _seen.Add(value);
    }
}
