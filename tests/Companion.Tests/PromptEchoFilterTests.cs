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
}
