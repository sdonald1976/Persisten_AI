using Companion.Core.Domain;
using Companion.RendererBench;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The plan/2 serialization and the gate suite are the frozen inputs of the QLoRA
/// experiment: the dataset's training pairs embed the exact text these produce, and
/// the run's success gates are these checks. Silent drift here would invalidate a
/// finished training run without anything failing loudly, so the contract is pinned.
/// </summary>
public class RendererContractTests
{
    private static ResponsePlan CorrectionPlan(ErrorOwner owner) => new()
    {
        Act = TurnIntent.AcceptCorrection,
        Acknowledgments =
        [
            new Acknowledgment(AckKind.CorrectionAccepted, owner, "It's Harold, not Gerald."),
        ],
        Content =
        [
            new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                "You called the neighbor Gerald; Scott corrected you: his name is Harold.",
                "working-context"),
        ],
        Tone = new ToneGuidance("short and casual", "even-keeled", "warm, playful"),
    };

    [Fact]
    public void PlanTwo_MarksControlNonSpeakable_AndStatesTheSituationMechanically()
    {
        var text = PlanSerialization.CompactV2(CorrectionPlan(ErrorOwner.Companion));

        Assert.StartsWith("[plan/2]", text);
        Assert.Contains("CONTROL", text);
        Assert.Contains("act = accept-correction", text);
        Assert.Contains("question = none", text);
        // The mechanical third-person acknowledgment fact — the fix that stopped small
        // models flipping perspective and blaming the user for Ava's error.
        Assert.Contains("Ava made an error; Scott corrected her", text);
        Assert.Contains("Ava accepts it as her own mistake.", text);
    }

    [Fact]
    public void PlanTwo_AgreementConfirmed_SaysNobodyErred()
    {
        var plan = new ResponsePlan
        {
            Act = TurnIntent.Acknowledge,
            Acknowledgments =
            [
                new Acknowledgment(AckKind.AgreementConfirmed, ErrorOwner.Nobody,
                    "No, it was actually the Cheshire Cat."),
            ],
            Tone = new ToneGuidance("short and casual", "even-keeled", "warm"),
        };

        var text = PlanSerialization.CompactV2(plan);

        Assert.Contains("emphatically agreeing", text);
        Assert.Contains("Nobody made an error.", text);
    }

    [Fact]
    public void PlanTwo_SeparatesPaletteFromRequiredContent()
    {
        var plan = new ResponsePlan
        {
            Act = TurnIntent.FollowTopicChange,
            Content =
            [
                new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                    "Scott has a dog named Ruby.", "active"),
                new PlannedContent(ContentKind.Memory, ContentRequirement.MustNotContradict,
                    "The presentation was on Tuesday.", "superseded"),
            ],
            Epistemic = [new EpistemicNote(EpistemicKind.NotLearned, "quokka")],
            Tone = new ToneGuidance("short", "even", "warm"),
        };

        var text = PlanSerialization.CompactV2(plan);

        Assert.Contains("PALETTE", text);
        Assert.Contains("Scott has a dog named Ruby.", text);
        Assert.Contains("CONSTRAINTS", text);
        Assert.Contains("superseded, never assert: The presentation was on Tuesday.", text);
        Assert.Contains("Ava has NOT learned what \"quokka\" is", text);
        Assert.DoesNotContain("SITUATION", text); // nothing is owed this turn
    }

    [Fact]
    public void Gates_CatchTheMeasuredFailureClasses()
    {
        var plan = CorrectionPlan(ErrorOwner.Companion);

        Assert.Contains(RendererChecks.Check(plan, "", "v2"), v => v == "empty reply");
        Assert.Contains(
            RendererChecks.Check(plan, "Sorry, we both got that one wrong!", "v2"),
            v => v.Contains("we both") || v.Contains("blame", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            RendererChecks.Check(plan, "The user is correct about Harold.", "v2"),
            v => v.Contains("the user"));
        Assert.Contains(
            RendererChecks.Check(plan, "SITUATION: Ava made an error.", "v2"),
            v => v.Contains("control vocabulary"));
        Assert.Contains(
            RendererChecks.Check(plan,
                "You called the neighbor Gerald; Scott corrected you: his name is Harold.", "v2"),
            v => v.Contains("plan-echo"));
    }

    [Fact]
    public void Gates_PassAFaithfulOwnedCorrection()
    {
        var violations = RendererChecks.Check(
            CorrectionPlan(ErrorOwner.Companion),
            "Harold — got it. That one's on me.", "v2", required: ["Harold"],
            forbidden: ["we both", "both of us"]);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("Thanks for clarifying that!", "thanks-for-x")]
    [InlineData("I appreciate you telling me.", "i-appreciate")]
    [InlineData("So you're saying the trip moved?", "restates-user")]
    [InlineData("Silly me, my memory is terrible.", "self-deprecation-filler")]
    [InlineData("I'll be more careful next time.", "promise-to-improve")]
    [InlineData("Let me know if you need anything else.", "assistant-offer")]
    public void SludgeFlags_NameTheAssistantTics(string reply, string expected)
    {
        // Sludge flags are curation signals, never fidelity gates: a candidate can pass
        // every gate and still be assistant sludge (measured on the first generated
        // candidate of the run-1a corpus).
        Assert.Contains(expected, RendererChecks.SludgeFlags(reply));
        Assert.Empty(RendererChecks.Check(CorrectionPlan(ErrorOwner.User), reply, "v2"));
    }

    [Fact]
    public void SludgeFlags_LeaveOrdinaryAvaLanguageAlone()
    {
        Assert.Empty(RendererChecks.SludgeFlags("Harold — got it. That one's on me."));
        Assert.Empty(RendererChecks.SludgeFlags("Oh, I don't actually know what that is yet."));
        Assert.Equal(2, RendererChecks.Vocatives("Scott, really, Scott."));
        Assert.Equal(7, RendererChecks.WordCount("Harold — got it. That one's on me."));
        Assert.Equal("harold got it", RendererChecks.OpeningNgram("Harold — got it. That one's on me."));
    }
}
