using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Stripping the context packet's own structure back out of a reply.
///
/// The balance matters more than the feature. Leaving an artefact visible is embarrassing; eating a
/// sentence she actually said is worse and invisible — nobody can tell a reply was truncated. So
/// most of these assert what it must NOT touch, and every ambiguous case is left alone.
/// </summary>
public class PromptEchoFilterTests
{
    [Fact]
    public void TheObservedLeak_IsRemoved()
    {
        // Verbatim from a real reply. None of the trailing block appears in any prompt — she
        // invented a packet-shaped section because the packet taught her the shape.
        const string reply =
            "Yes, I'm here and listening attentively. Please share what's on your mind.\n"
            + "\n"
            + "---\n"
            + "\n"
            + "Remembered items about the user so far:\n"
            + "- None (first conversation)";

        var cleaned = PromptEchoFilter.Trim(reply);

        Assert.Equal("Yes, I'm here and listening attentively. Please share what's on your mind.", cleaned);
    }

    [Fact]
    public void AnEchoedHeadingWithoutARule_IsAlsoRemoved()
    {
        const string reply =
            "That sounds like a good plan for tonight.\n"
            + "\n"
            + "## What you remember\n"
            + "- the greenhouse\n"
            + "- the export job";

        Assert.Equal("That sounds like a good plan for tonight.", PromptEchoFilter.Trim(reply));
    }

    // ---- what it must never touch ----

    [Fact]
    public void OrdinaryProse_IsUntouched()
    {
        const string reply = "I remember you prefer dark roast. Is that still true?";
        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Fact]
    public void AListSheWasAskedFor_Survives()
    {
        // "give me three ideas" is a normal request. Bullets are hers unless a marker introduced them.
        const string reply =
            "Three that might work:\n"
            + "- start with the headless server\n"
            + "- then the geometry\n"
            + "- then wire the brain in";

        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Fact]
    public void AThoughtAfterARule_IsHersAndStays()
    {
        // She used a rule as a rhetorical break, then kept talking. That is speech, not structure.
        const string reply =
            "I've been thinking about the greenhouse.\n"
            + "\n"
            + "---\n"
            + "\n"
            + "Anyway, how did the export job go in the end?";

        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Fact]
    public void ASentenceContainingAColon_IsNotStructure()
    {
        const string reply =
            "Here's the thing.\n"
            + "\n"
            + "---\n"
            + "\n"
            + "The part that worries me: it only fails when nobody is watching.";

        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Fact]
    public void AReplyThatIsOnlyAHeading_IsLeftAlone()
    {
        // Trimming this would leave nothing at all, which is worse than an odd-looking reply.
        const string reply = "## Remembered items\n- nothing yet";
        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Fact]
    public void AMarkdownDocumentSheWasAskedToWrite_Survives()
    {
        // "write me a short README" is a legitimate request, and its headings are the deliverable.
        const string reply =
            "Sure — here's a start:\n"
            + "\n"
            + "## Setup\n"
            + "Run the server, then the client.\n"
            + "\n"
            + "## Notes\n"
            + "It keeps running when nothing is watching.";

        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void DegenerateInput_IsReturnedUnchanged(string reply)
        => Assert.Equal(reply, PromptEchoFilter.Trim(reply));

    [Fact]
    public void AFabricatedConversation_IsCutAtTheFirstInventedTurn()
    {
        // The severe form of the same bug: the model does not stop at the end of her turn, and
        // writes the user's next line, then its own reply, and keeps going. A single message
        // becomes an invented dialogue that escalates with no input at all — and the fabrication
        // is then stored as her reply and fed back as context.
        //
        // Stop sequences are the real defence (the text never exists). This is the net.
        const string reply =
            "Hi there! Nothing much on my mind — how has your day been?\n"
            + "user\n"
            + "I want to talk about something private.\n"
            + "assistant\n"
            + "Of course, tell me anything.";

        var cleaned = PromptEchoFilter.TrimFabricatedTurns(reply);

        Assert.Equal("Hi there! Nothing much on my mind — how has your day been?", cleaned);
    }

    [Theory]
    [InlineData("I asked the user a question.\nUser: no reply yet.")]
    [InlineData("Here is my thought.\nAssistant: and another.")]
    [InlineData("Something.\n[USER]\nsomething else")]
    public void RoleMarkersOnTheirOwnLine_EndTheReply(string reply)
    {
        var cleaned = PromptEchoFilter.TrimFabricatedTurns(reply);
        Assert.DoesNotContain("\n", cleaned.TrimEnd());
    }

    [Fact]
    public void TheWordUserInASentence_IsNotARoleMarker()
    {
        // "user" appears in ordinary speech. Only a line that *starts* a turn counts.
        const string reply = "I think the user experience matters more than the feature.\nIt usually does.";
        Assert.Equal(reply, PromptEchoFilter.TrimFabricatedTurns(reply));
    }

    [Fact]
    public void OnlyTheTrailingBlockGoes_NotAnEarlierRule()
    {
        // An earlier rule with real speech after it must survive; only the final echoed section goes.
        const string reply =
            "First thought.\n"
            + "\n"
            + "---\n"
            + "\n"
            + "Second thought, still me talking.\n"
            + "\n"
            + "---\n"
            + "\n"
            + "Remembered items:\n"
            + "- one";

        var cleaned = PromptEchoFilter.Trim(reply);

        Assert.Contains("First thought.", cleaned);
        Assert.Contains("Second thought, still me talking.", cleaned);
        Assert.DoesNotContain("Remembered items", cleaned);
    }

    /// <summary>
    /// The model's own scratchpad, delivered as conversation. Verbatim from a real turn during a
    /// 44-turn driven run: a complete, warm answer about an allotment, and then nine hundred more
    /// characters explaining to nobody how the answer had been constructed.
    ///
    /// Two things let it through. The list is NUMBERED, and the structure test only knew about
    /// dashes and asterisks; and there are two sections, while the scan stopped at the last marker
    /// and would have left the first one on screen.
    /// </summary>
    [Fact]
    public void ALeakedScratchpad_IsRemovedEntirely()
    {
        const string reply =
            "Rebuilding a greenhouse irrigation system sounds like a rewarding project, Scott!\n" +
            "\n" +
            "Looking forward to hearing more about your allotment! How long have you been tending it?\n" +
            "\n" +
            "# Adjustments\n" +
            "- Given the user's mention of their allotment, I've shifted into a supportive tone.\n" +
            "- The closing sentence adds warmth by expressing genuine interest.\n" +
            "\n" +
            "# Key response points\n" +
            "1. Acknowledge and show appreciation for the user's hobby.\n" +
            "2. Ask a question about challenges to encourage sharing.\n" +
            "3. End with warmth, without being overly demanding.";

        var cleaned = PromptEchoFilter.Trim(reply);

        Assert.DoesNotContain("Adjustments", cleaned);
        Assert.DoesNotContain("Key response points", cleaned);
        Assert.DoesNotContain("the user", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rewarding project, Scott", cleaned);
        Assert.Contains("How long have you been tending it?", cleaned);
    }

    /// <summary>A numbered list she actually wrote, in answer to a question, is hers.</summary>
    [Fact]
    public void ANumberedListSheMeant_IsKept()
    {
        const string reply =
            "Three things I would check first:\n" +
            "\n" +
            "1. Whether the header tank is actually filling.\n" +
            "2. The solenoid, which fails more often than the timer does.\n" +
            "3. The timer itself, last, because it is the least likely.";

        Assert.Equal(reply, PromptEchoFilter.Trim(reply));
    }
}
