using Companion.Core.Services;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

using Request = FrameLifecycle.Request;

/// <summary>
/// The frame lifecycle: who owns frame truth, and what each transition costs.
///
/// The rule under test throughout is that `InCharacterDetector` suggests and routes but does
/// not decide. A detector that says "this looks like roleplay" is evidence about the shape of
/// a message, not a declaration that a story has begun.
/// </summary>
public class FrameLifecycleTests
{
    // ---- rule 1: a hint never creates frame truth ------------------------------------------

    [Fact]
    public void DetectedInCharacterMarkup_AloneNeverEntersAFrame()
    {
        var d = FrameLifecycle.Decide(Request.DetectedInCharacter, hasActiveSession: false);

        Assert.Null(d.Transition);
        Assert.False(d.StartsSession);
        Assert.Contains("hint only", d.Cause);
        // ...and therefore the turn is not fictional, so nothing downstream is suppressed.
        Assert.False(FrameLifecycle.IsFictionTurn(d));
    }

    [Fact]
    public void AnExplicitRequest_EntersAndStartsASession()
    {
        var d = FrameLifecycle.Decide(Request.ExplicitEnter, hasActiveSession: false);

        Assert.Equal(FrameTransition.enter, d.Transition);
        Assert.True(d.StartsSession);
        Assert.True(FrameLifecycle.IsFictionTurn(d));
    }

    [Fact]
    public void ReEnteringAnActiveFrame_IsJustContinuing()
    {
        var d = FrameLifecycle.Decide(Request.ExplicitEnter, hasActiveSession: true);

        Assert.Equal(FrameTransition.@continue, d.Transition);
        Assert.False(d.StartsSession);
    }

    // ---- rule 2: exits are generous ---------------------------------------------------------

    [Fact]
    public void AnExplicitExit_AlwaysExits_AndEndsTheSession()
    {
        var d = FrameLifecycle.Decide(Request.ExplicitExit, hasActiveSession: true);

        Assert.Equal(FrameTransition.exit, d.Transition);
        Assert.True(d.EndsSession);
    }

    [Fact]
    public void AnAmbiguousExit_ResolvesTowardExit()
    {
        // Continuing a scene someone has left is the worse failure, so ambiguity breaks that
        // way on purpose rather than by accident.
        var d = FrameLifecycle.Decide(Request.AmbiguousExit, hasActiveSession: true);

        Assert.Equal(FrameTransition.exit, d.Transition);
        Assert.True(d.EndsSession);
        Assert.Contains("resolved-toward-exit", d.Cause);
    }

    [Fact]
    public void TheExitTurn_IsNotAFictionTurn()
    {
        // Exiting restores real rules ON the exit turn, not the one after — so the exit turn
        // itself is already real, and its content is not suppressed as fictional.
        var d = FrameLifecycle.Decide(Request.ExplicitExit, hasActiveSession: true);

        Assert.False(FrameLifecycle.IsFictionTurn(d));
    }

    // ---- transitions that have nothing to act on --------------------------------------------

    [Theory]
    [InlineData(Request.ExplicitSwitch)]
    [InlineData(Request.ExplicitExit)]
    [InlineData(Request.AmbiguousExit)]
    public void TransitionsWithNoActiveSession_ProduceNoFrame(Request request)
    {
        var d = FrameLifecycle.Decide(request, hasActiveSession: false);

        Assert.Null(d.Transition);
        Assert.False(d.StartsSession);
        Assert.False(d.EndsSession);
    }

    [Fact]
    public void SwitchingInsideAFrame_StaysInTheFrame()
    {
        var d = FrameLifecycle.Decide(Request.ExplicitSwitch, hasActiveSession: true);

        Assert.Equal(FrameTransition.switchScene, d.Transition);
        Assert.False(d.EndsSession);
        Assert.True(FrameLifecycle.IsFictionTurn(d));
    }

    [Fact]
    public void AnOrdinaryTurnInsideAFrame_Continues()
    {
        var d = FrameLifecycle.Decide(Request.None, hasActiveSession: true);

        Assert.Equal(FrameTransition.@continue, d.Transition);
        Assert.True(FrameLifecycle.IsFictionTurn(d));
    }

    [Fact]
    public void AnOrdinaryTurnWithNoFrame_StaysOrdinary()
    {
        var d = FrameLifecycle.Decide(Request.None, hasActiveSession: false);

        Assert.Null(d.Transition);
        Assert.False(FrameLifecycle.IsFictionTurn(d));
    }

    // ---- the whole arc ----------------------------------------------------------------------

    [Fact]
    public void AFullArc_EnterContinueSwitchExit_BehavesAsDeclared()
    {
        var active = false;
        var seen = new List<FrameTransition?>();

        foreach (var request in new[]
                 {
                     Request.DetectedInCharacter,   // hint before anything explicit: no frame
                     Request.ExplicitEnter,
                     Request.None,
                     Request.ExplicitSwitch,
                     Request.ExplicitExit,
                     Request.None,                  // after the exit: ordinary again
                 })
        {
            var d = FrameLifecycle.Decide(request, active);
            if (d.StartsSession) active = true;
            if (d.EndsSession) active = false;
            seen.Add(d.Transition);
        }

        Assert.Equal(
            [null, FrameTransition.enter, FrameTransition.@continue,
             FrameTransition.switchScene, FrameTransition.exit, null],
            seen);
    }
}
