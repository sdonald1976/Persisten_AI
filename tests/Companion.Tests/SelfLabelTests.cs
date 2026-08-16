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

    // ---- narrated gestures ----

    [Theory]
    [InlineData("*smiles warmly* That's lovely to hear.", "That's lovely to hear.")]
    [InlineData("*giggles softly at your playful response* Oh, we're evenly matched!", "Oh, we're evenly matched!")]
    [InlineData("That's kind. *nods understandingly* Thank you.", "That's kind.  Thank you.")]
    public void NarratedGesturesAreRemoved(string reply, string expected)
    {
        // The prompt forbids these and it is not enough: measured by the soak harness, the rule
        // holds on neutral conversation and collapses on affectionate conversation, which is
        // exactly when a roleplay fine-tune pulls hardest toward performing a scene.
        var cleaned = PromptEchoFilter.TrimStageDirections(reply);
        Assert.Equal(expected.Replace("  ", " ").Trim(), cleaned.Replace("  ", " ").Trim());
    }

    [Theory]
    [InlineData("I *really* think you should.")]
    [InlineData("That's *not* what I meant.")]
    [InlineData("It was *that* obvious?")]
    public void EmphasisIsHersAndSurvives(string reply)
    {
        // The whole safety margin is length and word count. Short emphasis is punctuation; a long
        // lower-case phrase is a body she does not have.
        Assert.Equal(reply, PromptEchoFilter.TrimStageDirections(reply));
    }

    [Fact]
    public void AReplyThatIsOnlyAGestureIsKept()
        => Assert.Equal("*smiles warmly at you*", PromptEchoFilter.TrimStageDirections("*smiles warmly at you*"));

    [Fact]
    public void GesturesNeverReachTheStream()
    {
        // Same reason as the label: the client keeps the streamed tokens and speech is synthesized
        // from them, so a gesture removed afterwards is still read and still spoken.
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        foreach (var chunk in new[] { "Ava: That sound", "s lovely. *smiles", " warmly* Tell me mo", "re about it." })
            sink.Report(chunk);
        sink.Flush();

        var text = string.Concat(seen).Replace("  ", " ");
        Assert.Contains("That sounds lovely.", text);
        Assert.Contains("Tell me more about it.", text);
        Assert.DoesNotContain("smiles", text);
    }

    [Fact]
    public void AnUnclosedAsteriskIsNotSwallowed()
    {
        // A reply ending mid-span must still arrive. Holding it back forever would lose her words
        // to a filter meant to protect them.
        var seen = new List<string>();
        var sink = new SelfLabelSink(new Collector(seen), "Ava");

        sink.Report("Five stars out of *five for that curry, honestly one of the best");
        sink.Flush();

        Assert.Contains("five for that curry", string.Concat(seen));
    }
}
