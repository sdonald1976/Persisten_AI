using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The production side of the render-eligibility contract: the shadow evidence row.
///
/// This is the path Run-2's corpus is collected through, and it is where the old contract
/// actually hurt. <c>PersistableIdentity</c> called <c>RenderPromptHash</c>, which called
/// <c>CompactV3</c>, which threw for a render-ineligible plan. The throw escaped
/// <c>WithNative</c>, was swallowed by the shadow service's outer catch, incremented a generic
/// failure counter and logged at Debug — so the entire turn's V3 evidence vanished and nothing
/// recorded WHY. A plan the corpus most needs to know about was the one plan it could not see.
///
/// Now eligibility is asked before anything is serialized, and the refusal is data on the row.
/// </summary>
public class RenderEligibilityEvidenceTests
{
    private static readonly RendererTrustContext Trust = new(RendererTransport.local_loopback);

    /// <summary>An ordinary plan: nothing in it coaches the renderer.</summary>
    private static ResponsePlan Eligible() => new()
    {
        TraceId = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001"),
        Act = TurnIntent.Acknowledge,
        Content =
        [
            new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                "The synthetic kettle finished boiling.", "working-context"),
        ],
        Tone = new ToneGuidance("short and casual", null, null),
    };

    /// <summary>
    /// The same plan plus ONE producer-authored item whose text coaches. Derived from the
    /// eligible plan rather than written from scratch, so the only difference between the two
    /// is the thing under test. "own it" is one of the phrases the lint has always caught, and
    /// source "working-context" is one of the three authored sources it applies to — quoting or
    /// a non-authored source would (correctly) exempt it.
    /// </summary>
    private static PlanV3.PlanV3 Ineligible(PlanV3.PlanV3 eligible) => eligible with
    {
        Items =
        [
            .. eligible.Items,
            new PlanItem
            {
                Id = "q2",
                Type = "coaching-probe",
                Policy = ExpressionPolicy.may_express,
                Text = "She got the date wrong. Own it and say so plainly.",
                Source = "working-context",
            },
        ],
    };

    [Fact]
    public void AnIneligibleNativePlan_IsRecordedAsEvidence_NotLostToAnException()
    {
        var translated = V2Translation.FromV2(Eligible());
        var native = Ineligible(translated);

        // The precondition that makes this test meaningful: valid, but not renderable.
        Assert.Empty(PlanV3Codec.Validate(native));
        Assert.False(PlanV3Codec.CheckRenderEligibility(native).Eligible);

        var envelope = V3ShadowEnvelopeBuilder.Build(
            Eligible(), translated, null, 0, ["usr-local"], Trust);

        // This is the call that used to throw.
        var withNative = V3ShadowEnvelopeBuilder.WithNative(
            envelope, translated, native, null, [], null, 0, ["usr-local"], Trust);

        Assert.NotNull(withNative.Native);
        var section = withNative.Native!;
        Assert.True(section.Valid);                       // structurally fine...
        Assert.False(section.RenderEligible);             // ...but refused for rendering
        Assert.NotNull(section.RenderRefusals);
        Assert.NotEmpty(section.RenderRefusals!);
        Assert.All(section.RenderRefusals!,
            r => Assert.Contains("rule=producer-coaching", r, StringComparison.Ordinal));

        // Structural identity survives; the missing render hash is the finding, not a crash.
        Assert.NotNull(section.WirePlanHash);
        Assert.Null(section.RenderPromptHash);
    }

    [Fact]
    public void TheRecordedRefusals_CarryNoProducerText()
    {
        var translated = V2Translation.FromV2(Eligible());
        var native = Ineligible(translated);
        var envelope = V3ShadowEnvelopeBuilder.WithNative(
            V3ShadowEnvelopeBuilder.Build(Eligible(), translated, null, 0, ["usr-local"], Trust),
            translated, native, null, [], null, 0, ["usr-local"], Trust);

        // The row must be able to say a plan was refused without repeating what it said.
        foreach (var reason in envelope.Native!.RenderRefusals!)
        {
            Assert.DoesNotContain("Own it", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("got the date wrong", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnEligiblePlan_RecordsEligibilityAndKeepsItsRenderPromptHash()
    {
        var translated = V2Translation.FromV2(Eligible());
        var envelope = V3ShadowEnvelopeBuilder.Build(
            Eligible(), translated, null, 0, ["usr-local"], Trust);

        Assert.True(envelope.RenderEligible);
        Assert.Empty(envelope.RenderRefusals);
        Assert.NotNull(envelope.RenderPromptHash);
    }
}
