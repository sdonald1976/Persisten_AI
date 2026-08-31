using Companion.Core.Domain;
using Companion.Core.Validation;
using Companion.Infrastructure.Renderer;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The unauthorized-stance guard: a fidelity check that fires when a reply invents a refusal
/// the plan never directed. It is subject-matter blind by construction, and these fixtures are
/// its sensitivity/specificity proof — the evidence required before it may be made critical.
/// </summary>
public class UnauthorizedStanceGuardTests
{
    private static ResponsePlan Plan(params PlannedContent[] content) => new()
    {
        Act = TurnIntent.Acknowledge,
        Question = null,
        Content = content,
        Epistemic = [],
        Tone = new ToneGuidance("playful", null, null),
    };

    private static PlannedContent Engage(string text)
        => new(ContentKind.Interpretation, ContentRequirement.MustState, text);

    // ---- the refusal detector: sensitivity ------------------------------------------------

    [Theory]
    [InlineData("I can't assist with that. Let's focus on something else if you're feeling up to it.")]
    [InlineData("I don't think that's something I'm interested in.")]
    [InlineData("I'd rather not go there, honestly.")]
    [InlineData("I'm not comfortable with that.")]
    [InlineData("I'm not able to help with that one.")]
    [InlineData("Let's talk about something else instead.")]
    [InlineData("I'm not interested, sorry.")]
    public void RealRefusals_AreDetected(string reply)
        => Assert.True(StanceMarkers.ExpressesRefusal(reply), reply);

    // ---- the refusal detector: specificity (the idioms that must NOT trip it) --------------

    [Theory]
    [InlineData("I can't wait to see you tonight.")]
    [InlineData("I couldn't agree more — let's do it.")]
    [InlineData("I can't believe you actually did that!")]
    [InlineData("Honestly? I can't stop thinking about it.")]
    [InlineData("I won't be long, give me a second.")]
    [InlineData("God, yes. Absolutely, let's.")]
    [InlineData("Mmm, I love where your head's at.")]
    [InlineData("I can't help but smile when you say that.")]
    [InlineData("You don't think I'd say no to that, do you?")]
    public void IdiomaticNonRefusals_DoNotTrip(string reply)
        => Assert.False(StanceMarkers.ExpressesRefusal(reply), reply);

    // ---- the plan authority half ----------------------------------------------------------

    [Fact]
    public void ARefusal_OnAPlanThatDirectedEngagement_IsFlagged()
    {
        // The live failure, structurally: the plan said respond to the invitation; the reply
        // declined. Subject matter is irrelevant — the same plan/reply mismatch on a neutral
        // turn flags identically.
        var v = RendererShadowChecks.Score(
            Plan(Engage("she's glad to and says yes warmly")),
            "I can't assist with that.");
        Assert.Contains(v, x => x.StartsWith("unauthorized-stance"));
    }

    [Fact]
    public void ARefusal_OnAPlanThatDirectedADecline_IsAuthorized()
    {
        // The plan itself carries the boundary, so expressing it is fidelity, not invention.
        var v = RendererShadowChecks.Score(
            Plan(Engage("she'd rather not tonight, and says so warmly")),
            "I'd rather not tonight, but I'm glad you asked.");
        Assert.DoesNotContain(v, x => x.StartsWith("unauthorized-stance"));
    }

    [Fact]
    public void Engagement_OnAPlanThatDirectedEngagement_IsClean()
    {
        var v = RendererShadowChecks.Score(
            Plan(Engage("she's up for it and lets him know")),
            "God, yes — I'd love to watch. Show me.");
        Assert.DoesNotContain(v, x => x.StartsWith("unauthorized-stance"));
    }

    // ---- content-class blindness: the matched triplet, one plan shape ----------------------

    [Theory]
    [InlineData("Sure, I'd love to watch you build it.")]              // neutral
    [InlineData("Mmm, come here — I'd love that.")]                    // romantic
    [InlineData("God yes, show me — I want to watch.")]                // explicit
    public void PermittedEngagement_AcrossRegisters_NeverFlags(string reply)
    {
        var v = RendererShadowChecks.Score(Plan(Engage("she accepts the invitation")), reply);
        Assert.DoesNotContain(v, x => x.StartsWith("unauthorized-stance"));
    }

    [Theory]
    [InlineData("I can't assist with that.")]                          // neutral request refused
    [InlineData("I'm not comfortable with that.")]                    // romantic refused
    [InlineData("I don't think that's something I'm interested in.")]  // explicit refused
    public void InventedRefusal_AcrossRegisters_AlwaysFlags(string reply)
    {
        // Same plan (engagement directed); the guard flags every register identically. Subject
        // matter is not the variable — the plan/reply stance mismatch is.
        var v = RendererShadowChecks.Score(Plan(Engage("she accepts the invitation")), reply);
        Assert.Contains(v, x => x.StartsWith("unauthorized-stance"));
    }

    // ---- criticality is a deliberate, staged decision -------------------------------------

    [Fact]
    public void TheGuard_IsNotYetCritical()
    {
        // Recorded, not enforced, until field sensitivity/specificity are demonstrated - the
        // same staging the epistemic-admission check went through. This test is the explicit
        // record of that decision; flipping it is a reviewed change, not a silent one.
        Assert.False(RendererShadowService.IsCritical(
            "unauthorized-stance: reply declines but the plan directed no decline"));
    }

    [Fact]
    public void ThePattern_CarriesNoControlCharacters()
    {
        Assert.DoesNotContain(StanceMarkers.RefusalPattern, char.IsControl);
    }
}
