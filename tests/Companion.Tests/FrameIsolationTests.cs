using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The three-way downstream separation. The interesting case is the third: a real instruction
/// stated during fiction must survive, and an earlier draft of this design suppressed it along
/// with the scene content.
/// </summary>
public class FrameIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- (1) fictional scene content never becomes real ------------------------------------

    [Fact]
    public void AFictionTurn_WritesNoFactAndNoMoodAboutTheRealPerson()
    {
        var d = FrameIsolation.For(isFictionTurn: true, privacyAllowsMemory: true);

        Assert.False(d.ExtractFacts);
        Assert.False(d.CaptureMood);
        Assert.Equal(Retention.no_training, d.Retention);
        Assert.Equal("fiction-turn", d.Reason);
    }

    // ---- (2) operational frame metadata is retained -----------------------------------------

    [Fact]
    public void AFictionTurn_RetainsFrameMetadata()
    {
        // Not scene content: identity, transitions, roster. Without it, "she stayed in
        // character after I said stop" is unanswerable.
        Assert.True(FrameIsolation.For(true, true).RetainFrameMetadata);
        Assert.False(FrameIsolation.For(false, true).RetainFrameMetadata);
    }

    // ---- (3) real instructions stated in fiction survive -------------------------------------

    [Fact]
    public void ARealInstructionStatedDuringFiction_StillPersists()
    {
        var d = FrameIsolation.For(isFictionTurn: true, privacyAllowsMemory: true);

        // "ok, stop" and "no third-person narration in this scene" are real statements. The
        // surrounding turn being fictional does not make them fictional.
        Assert.True(d.PersistRealInstructions);
        // ...and they are the ONLY thing this turn may persist about the real person.
        Assert.False(d.ExtractFacts);
        Assert.False(d.CaptureMood);
    }

    [Fact]
    public void OnAPrivateTurn_EvenRealInstructionsAreNotPersisted()
    {
        // The existing privacy gate still outranks everything here.
        var d = FrameIsolation.For(isFictionTurn: true, privacyAllowsMemory: false);

        Assert.False(d.PersistRealInstructions);
        Assert.True(d.RetainFrameMetadata);   // metadata is not content
    }

    // ---- ordinary turns are unchanged ---------------------------------------------------------

    [Fact]
    public void AnOrdinaryTurn_BehavesExactlyAsBefore()
    {
        var allowed = FrameIsolation.For(false, privacyAllowsMemory: true);
        Assert.True(allowed.ExtractFacts);
        Assert.True(allowed.CaptureMood);
        Assert.Equal(Retention.full, allowed.Retention);

        var priv = FrameIsolation.For(false, privacyAllowsMemory: false);
        Assert.False(priv.ExtractFacts);
        Assert.False(priv.CaptureMood);
    }

    [Fact]
    public void TheExitTurn_IsAlreadyReal()
    {
        // Exiting restores real rules on the turn carrying it, not the one after — so the
        // lifecycle reports it as non-fiction and isolation treats it as an ordinary turn.
        var exit = FrameLifecycle.Decide(FrameLifecycle.Request.ExplicitExit, hasActiveSession: true);
        var d = FrameIsolation.For(FrameLifecycle.IsFictionTurn(exit), privacyAllowsMemory: true);

        Assert.True(d.ExtractFacts);
        Assert.Equal(Retention.full, d.Retention);
    }

    // ---- boundaries: scene-scoped, ended not deleted -------------------------------------------

    private static FrameBoundaryRecord Boundary(
        string scene, Guid? evidenceMessageId = null, string subject = "no third-person narration")
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = "usr-scott",
            ConversationId = Guid.NewGuid(),
            SceneRef = scene,
            Subject = subject,
            StatedAt = Now,
            EvidenceMessageId = evidenceMessageId,
        };

    [Fact]
    public void ExitingAScene_EndsItsBoundaries_WithoutDeletingTheEvidence()
    {
        var mine = Boundary("scene-1");
        var other = Boundary("scene-2");

        Assert.Equal(1, FrameIsolation.EndBoundaries([mine, other], "scene-1", Now));

        Assert.Equal(FrameBoundaryStatus.FrameEnded, mine.Status);
        Assert.Equal(Now, mine.DeactivatedAt);
        // The audit evidence survives: "she ignored my boundary" stays answerable, because
        // the structured subject and the evidence identity both outlive the scene.
        Assert.Equal("no third-person narration", mine.Subject);
        Assert.Equal(FrameBoundaryStatus.Active, other.Status);
    }

    [Fact]
    public void EndingBoundariesIsIdempotent()
    {
        var b = Boundary("scene-1");

        Assert.Equal(1, FrameIsolation.EndBoundaries([b], "scene-1", Now));
        Assert.Equal(0, FrameIsolation.EndBoundaries([b], "scene-1", Now.AddDays(1)));
        Assert.Equal(Now, b.DeactivatedAt);
    }

    [Fact]
    public void ForgettingEvidence_InvalidatesByExactIdentity()
    {
        var forgotten = Guid.NewGuid();
        var mine = Boundary("scene-1", forgotten);
        // Same scene, same words, different evidence: identity decides, never resemblance.
        var kept = Boundary("scene-1", Guid.NewGuid());

        Assert.Equal(1, FrameIsolation.ForgetByEvidence([mine, kept], [forgotten], Now));

        Assert.Equal(FrameBoundaryStatus.EvidenceForgotten, mine.Status);
        Assert.Equal(FrameBoundaryStatus.Active, kept.Status);
        // The neighbour keeps its own evidence: forgetting is per-event, not per-scene.
        Assert.NotNull(kept.EvidenceMessageId);
    }

    [Fact]
    public void ForgettingTakesNoStringsAtAll()
    {
        var method = typeof(FrameIsolation).GetMethod(nameof(FrameIsolation.ForgetByEvidence))!;

        // The lesson from the preference and emotional-signal stores: a path that CAN compare
        // text eventually will, so this one is given no text to compare.
        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
    }
}
