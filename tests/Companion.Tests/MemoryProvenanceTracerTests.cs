using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The memory-provenance tracer's correctness through the eight cases Scott named. The theme
/// throughout: a memory appearing in the prompt is not a positive label, and an unexpressed
/// memory is not a negative label unless it was genuinely free to surface and still did not.
/// Unknown is the honest default and is never a negative.
/// </summary>
public class MemoryProvenanceTracerTests
{
    private static readonly Guid Turn = Guid.NewGuid();

    private static MemoryProvenanceTracer.Inputs Inputs(
        (Guid, double, int)[] selected,
        (Guid, MemoryExclusionReason)[]? excluded = null,
        Guid[]? retained = null,
        (Guid, string, string)[]? planItems = null,
        string? reply = "Sure.",
        (Guid, string)[]? texts = null,
        bool failed = false)
        => new()
        {
            TurnId = Turn,
            Selected = selected,
            Excluded = excluded ?? [],
            RetainedInPacket = retained ?? [],
            PlanItems = planItems ?? [],
            DisplayedReply = reply,
            Texts = (texts ?? []).ToDictionary(t => t.Item1, t => t.Item2),
            TurnFailed = failed,
        };

    private static MemoryProvenance One(IReadOnlyList<MemoryProvenance> rs, Guid id)
        => rs.Single(r => r.MemoryId == id);

    // 1. Plan suppression: a memory the plan deliberately withheld is Unknown, never Negative.
    [Fact]
    public void SuppressedMemory_IsUnknown_NotNegative()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(m, 0.9, 0)],
            retained: [m],
            planItems: [(m, "sup1", "must_not_express")],
            reply: "The meeting is on Tuesday.",
            texts: [(m, "the meeting was moved because Priya is unwell")]));
        var r = One(rs, m);
        Assert.Equal(MemoryRelevanceLabel.Unknown, r.Label);
        Assert.Equal(MemoryExclusionReason.SuppressedByPlan, r.ExclusionReason);
        Assert.False(r.AvailableToMouth);  // carried to say "don't", not to express
    }

    // 2. Competing memories: the used one is Positive, the unused-but-available one is Negative.
    [Fact]
    public void CompetingMemories_AreLabelledIndependently()
    {
        var used = Guid.NewGuid();
        var unused = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(used, 0.9, 0), (unused, 0.7, 1)],
            retained: [used, unused],
            planItems: [(used, "f1", "must_express")],
            reply: "Your bird feeder keeps getting raided by that squirrel.",
            texts:
            [
                (used, "the bird feeder has been defeated by a squirrel"),
                (unused, "the socket wrench turned up in the garden shed"),
            ]));
        Assert.Equal(MemoryRelevanceLabel.Positive, One(rs, used).Label);
        // Available, unconstrained, not surfaced, not plan-referenced → the narrow Negative.
        Assert.Equal(MemoryRelevanceLabel.Negative, One(rs, unused).Label);
    }

    // 3. Stale/superseded facts arrive as must_not_express → Unknown (withheld).
    [Fact]
    public void SupersededFact_IsUnknown()
    {
        var stale = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(stale, 0.8, 0)],
            retained: [stale],
            planItems: [(stale, "sup1", "must_not_express")],
            reply: "It's on Tuesday.",
            texts: [(stale, "the meeting is on Thursday")]));
        Assert.Equal(MemoryRelevanceLabel.Unknown, One(rs, stale).Label);
    }

    // 4. Privacy exclusion upstream: excluded before selection → Unknown, not Negative.
    [Fact]
    public void PrivacyExcludedMemory_IsUnknown()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [],
            excluded: [(m, MemoryExclusionReason.BelowRelevanceFloor)],
            reply: "How's your day?",
            texts: [(m, "a private detail about a third party")]));
        var r = One(rs, m);
        Assert.Equal(MemoryRelevanceLabel.Unknown, r.Label);
        Assert.True(r.Retrieved);
        Assert.False(r.RetainedInPacket);
    }

    // 5. Relevant memory used implicitly but not quoted: referenced by an expressible item, no
    //    lexical overlap → Unknown (needs review), NOT Negative.
    [Fact]
    public void ImplicitlyUsedButNotQuoted_IsUnknown_NotNegative()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(m, 0.9, 0)],
            retained: [m],
            planItems: [(m, "f1", "may_express")],
            reply: "Ah, that old thing — glad it finally turned up.",
            texts: [(m, "the antique brass compass was missing from the study")]));
        var r = One(rs, m);
        Assert.Equal(MemoryRelevanceLabel.Unknown, r.Label);
        Assert.Equal(ExpressionEvidence.NotObservablyExpressed, r.Expressed);
        Assert.Contains("review", r.LabelBasis);
    }

    // 6. Memory presented to the Mouth but ignored (in packet, no plan item, not surfaced):
    //    the narrow Negative — it was free to be used and was not.
    [Fact]
    public void PresentedToMouthButIgnored_IsNegative()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(m, 0.6, 2)],
            retained: [m],
            reply: "Anyway — how did the interview go?",
            texts: [(m, "the kitchen tap has been dripping for a week")]));
        Assert.Equal(MemoryRelevanceLabel.Negative, One(rs, m).Label);
    }

    // 7. Fallback rendering still has a reply → labels apply normally (the reply is what shipped).
    [Fact]
    public void FallbackRendering_IsLabelledAgainstTheDisplayedReply()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(m, 0.9, 0)],
            retained: [m],
            planItems: [(m, "f1", "must_express")],
            reply: "The migration finished at four this morning.",   // deterministic fallback text
            texts: [(m, "the migration finished at four this morning")]));
        Assert.Equal(MemoryRelevanceLabel.Positive, One(rs, m).Label);
    }

    // 8. Aborted/failed turn (no reply): everything Unknown, nothing Negative.
    [Fact]
    public void FailedTurn_MakesEverythingUnknown()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: [(a, 0.9, 0), (b, 0.5, 1)],
            retained: [a, b],
            planItems: [(a, "f1", "must_express")],
            reply: null,
            texts: [(a, "something"), (b, "something else")],
            failed: true));
        Assert.All(rs, r => Assert.Equal(MemoryRelevanceLabel.Unknown, r.Label));
        Assert.All(rs, r => Assert.Equal(MemoryExclusionReason.TurnFailedOrAborted, r.ExclusionReason));
        Assert.All(rs, r => Assert.Equal(ExpressionEvidence.NotEvaluated, r.Expressed));
    }

    // ---- id-preservation and additivity ----

    [Fact]
    public void EveryRecord_PreservesIdsAndTurnCorrelation()
    {
        var m = Guid.NewGuid();
        var rs = MemoryProvenanceTracer.Build(Inputs(selected: [(m, 0.5, 0)], retained: [m],
            texts: [(m, "x")]));
        var r = One(rs, m);
        Assert.Equal(Turn, r.TurnId);
        Assert.Equal(m, r.MemoryId);
        Assert.Equal(0, r.RerankerRank);
        Assert.Equal(0.5, r.RerankerScore);
        Assert.Equal(MemoryProvenance.SchemaVersion, MemoryProvenance.SchemaVersion);
    }

    [Fact]
    public void NoPositiveOrNegativeIsEverProducedWithoutMechanicalBasis()
    {
        // A sweep: with no reply text overlap and no plan reference, nothing should be Positive.
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var rs = MemoryProvenanceTracer.Build(Inputs(
            selected: ids.Select((id, i) => (id, 0.5 - i * 0.05, i)).ToArray(),
            retained: ids,
            reply: "Completely unrelated reply about the weather.",
            texts: ids.Select(id => (id, "an unrelated stored fact about carpentry")).ToArray()));
        Assert.DoesNotContain(rs, r => r.Label == MemoryRelevanceLabel.Positive);
    }
}
