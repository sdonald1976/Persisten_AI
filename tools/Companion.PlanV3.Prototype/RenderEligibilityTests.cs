using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// The contract that ended "valid does not mean renderable".
///
/// Before this existed, <see cref="PlanV3Codec.Validate"/> could pass a plan that
/// <see cref="PlanV3Codec.CompactV3"/> then refused by throwing, and no caller could tell the
/// difference without catching an exception. Two corpus plans sit exactly on that seam. They
/// are pinned here as the worked example of all three layers agreeing:
///
///   1. structurally valid                       — Validate returns no errors
///   2. render-INELIGIBLE with explicit reasons  — CheckRenderEligibility names item and rule
///   3. the serializer refuses for THOSE reasons — CompactV3/CompactV4 throw the typed
///                                                 exception carrying the same refusals
///
/// The point of (3) is that the serializer no longer knows a rule the caller could not ask
/// about: its refusal and the eligibility answer are the same object.
/// </summary>
public class RenderEligibilityTests
{
    /// <summary>The two frozen-corpus plans that are valid but not renderable.</summary>
    public static TheoryData<string> IneligiblePlanIds => new() { "epc-class-02", "epc-gadget-06" };

    private static PlanV3 Translate(string id)
    {
        var (_, plan, _) = CorpusGoldenTests.CorpusPlans().Single(p => p.Id == id);
        return V2Translation.FromV2(plan);
    }

    [Theory]
    [MemberData(nameof(IneligiblePlanIds))]
    public void TheRefusedFixtures_AreStructurallyValid(string id)
    {
        // Layer 1. If this ever fails, the plan became malformed and the fixture is no longer
        // demonstrating the seam it was chosen for.
        Assert.Empty(PlanV3Codec.Validate(Translate(id)));
    }

    [Theory]
    [MemberData(nameof(IneligiblePlanIds))]
    public void TheRefusedFixtures_AreRenderIneligible_WithExplicitTypedReasons(string id)
    {
        // Layer 2. Asked without throwing, and the answer names the item and the rule.
        var eligibility = PlanV3Codec.CheckRenderEligibility(Translate(id));

        Assert.False(eligibility.Eligible);
        var refusal = Assert.Single(eligibility.Refusals);
        Assert.Equal("q2", refusal.ItemId);
        Assert.Equal("working-context", refusal.Source);
        Assert.Equal(RenderRefusalCodes.ProducerCoaching, refusal.Code);
        Assert.Equal("q2 source=working-context rule=producer-coaching", refusal.ToString());
    }

    [Theory]
    [MemberData(nameof(IneligiblePlanIds))]
    public void CompactV3_RefusesForExactlyThoseReasons(string id)
    {
        // Layer 3. The serializer's refusal IS the eligibility answer — same codes, same items,
        // same order — rather than a second rule discovered at serialization time.
        var plan = Translate(id);
        var expected = PlanV3Codec.CheckRenderEligibility(plan);

        var ex = Assert.Throws<PlanNotRenderableException>(() => PlanV3Codec.CompactV3(plan));

        Assert.Equal(expected.Refusals, ex.Refusals);
        Assert.Equal(expected.Reasons, ex.Eligibility.Reasons);
    }

    [Theory]
    [MemberData(nameof(IneligiblePlanIds))]
    public void CompactV4_RefusesForExactlyTheSameReasons(string id)
    {
        // plan/4 consults the same check rather than reimplementing the lint, so it cannot
        // acquire a serialization rule that plan/3 callers have no way to ask about.
        var plan = Translate(id);
        var expected = PlanV3Codec.CheckRenderEligibility(plan);

        var ex = Assert.Throws<PlanNotRenderableException>(() => PlanV4Codec.CompactV4(plan));

        Assert.Equal(expected.Refusals, ex.Refusals);
    }

    [Theory]
    [MemberData(nameof(IneligiblePlanIds))]
    public void ARefusedPlanStillProducesStructuralIdentity_ButNoRenderPromptHash(string id)
    {
        // The production evidence path calls this. It used to THROW here, which discarded the
        // whole shadow row and recorded only that something failed. Now the row survives and
        // the absent hash is itself the finding.
        var identity = PlanV3Codec.PersistableIdentity(Translate(id));

        Assert.NotNull(identity.WirePlanHash);
        Assert.Null(identity.RenderPromptHash);
    }

    [Fact]
    public void TheRefusedException_NeverCarriesTheOffendingText()
    {
        // The coaching phrase is producer-authored text. A refusal that quoted it would put
        // that text into log lines and shadow rows through the exception message.
        var plan = Translate("epc-class-02");
        var coachingItem = plan.Items.Single(i => i.Id == "q2");
        Assert.NotNull(coachingItem.Text);

        var ex = Assert.Throws<PlanNotRenderableException>(() => PlanV3Codec.CompactV3(plan));

        Assert.DoesNotContain(coachingItem.Text!, ex.Message, StringComparison.Ordinal);
        foreach (var reason in ex.Eligibility.Reasons)
            Assert.DoesNotContain(coachingItem.Text!, reason, StringComparison.Ordinal);
    }

    // ---- the eligible side of the contract ---------------------------------------------------

    [Fact]
    public void TheOverwhelmingMajorityOfTheCorpusIsRenderEligible()
    {
        // Guards the inverse mistake: an over-broad eligibility rule that starts refusing
        // ordinary plans would still make every assertion above pass.
        var valid = CorpusGoldenTests.CorpusPlans()
            .Select(p => V2Translation.FromV2(p.Plan))
            .Where(v3 => PlanV3Codec.Validate(v3).Count == 0)
            .ToList();
        var ineligible = valid.Count(v3 => !PlanV3Codec.CheckRenderEligibility(v3).Eligible);

        Assert.Equal(2, ineligible);
    }

    [Fact]
    public void EligibilityIsPureAndNeverThrows_EvenForAStructurallyBrokenPlan()
    {
        // Callers are told to ask BEFORE serializing, so the asking itself must be safe on a
        // plan that has not been validated yet.
        var broken = Translate("epc-class-02") with { Participants = [] };

        Assert.NotEmpty(PlanV3Codec.Validate(broken));
        var eligibility = PlanV3Codec.CheckRenderEligibility(broken);
        Assert.False(eligibility.Eligible);
    }

    [Fact]
    public void StructuralErrorsAreStillValidationErrors_NotRenderRefusals()
    {
        // Validate's semantics are unchanged: a malformed plan throws the plain
        // InvalidOperationException it always threw, not the render-refusal type.
        var broken = Translate("epc-class-02") with { Participants = [] };

        var ex = Assert.Throws<InvalidOperationException>(() => PlanV3Codec.CompactV3(broken));
        Assert.IsNotType<PlanNotRenderableException>(ex);
        Assert.StartsWith("invalid plan:", ex.Message, StringComparison.Ordinal);
    }
}
