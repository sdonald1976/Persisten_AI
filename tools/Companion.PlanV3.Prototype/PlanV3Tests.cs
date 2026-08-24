using System.Text.Json.Nodes;
using Companion.Core.Domain;
using Companion.RendererBench;
using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// The prototype's proof obligations (docs/RESPONSE_PLAN_V3_SPEC.md):
/// round-trips, the extensibility rules of §4.3, the coaching lint of §2.4, the
/// determinism of §3.5, and — the migration hinge — v2→v3→v2 reproducing
/// byte-identical CompactV2 for real corpus plans.
/// </summary>
public class PlanV3Tests
{
    private static PlanV3 Sample() => new()
    {
        TraceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Participants = new Participants("Scott", "Ava"),
        Act = "accept-correction",
        Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "q1"),
        Items =
        [
            new PlanItem { Id = "c1", Type = "correction", Policy = ExpressionPolicy.must_express,
                Text = "Ava said the workshop was Tuesday; it is Thursday.",
                Value = JsonNode.Parse("{\"owner\":\"self\"}"), Source = "working-context",
                Supersedes = ["mem-4821"] },
            new PlanItem { Id = "s1", Type = "superseded", Policy = ExpressionPolicy.must_not_express,
                Text = "The workshop is on Tuesday.", SupersededBy = "c1", Source = "supersession" },
            new PlanItem { Id = "q1", Type = "clarify", Policy = ExpressionPolicy.ask_required,
                Text = "which list is meant — groceries or hardware", Source = "working-context" },
            new PlanItem { Id = "v1", Type = "observation", Policy = ExpressionPolicy.background_only,
                Text = "Rain streaks the window behind Scott.", Source = "vision", Confidence = 0.82 },
            new PlanItem { Id = "m1", Type = "memory", Policy = ExpressionPolicy.may_express,
                Text = "Scott is repainting the office a color he likes.", Source = "retrieval" },
        ],
        Register = new RegisterVector { Warmth = "warm", Verbosity = "terse", Mirror = false },
        Extensions = (JsonObject?)JsonNode.Parse("{\"dream-journal\":{\"entries\":3}}"),
    };

    [Fact]
    public void JsonRoundTrip_PreservesEverything_IncludingUnknownExtensions()
    {
        var plan = Sample();
        var report = PlanV3Codec.Parse(PlanV3Codec.ToJson(plan));

        Assert.Empty(report.RejectedItems);
        Assert.Equal(["dream-journal"], report.UnknownExtensionBlocks);
        Assert.Equal(PlanV3Codec.ToJson(plan), PlanV3Codec.ToJson(report.Plan));
    }

    [Fact]
    public void UnknownPolicy_RejectsTheItem_FailClosed_AndNamesIt()
    {
        var json = PlanV3Codec.ToJson(Sample())
            .Replace("\"policy\":\"may_express\"", "\"policy\":\"must_whisper\"");

        var report = PlanV3Codec.Parse(json);

        Assert.Contains(report.RejectedItems, r => r.Contains("must_whisper"));
        Assert.DoesNotContain(report.Plan.Items, i => i.Id == "m1");
        // Fail closed: the unknown policy did not silently become an obligation.
        Assert.DoesNotContain(report.Plan.Items, i => i.Policy == ExpressionPolicy.must_express && i.Id == "m1");
    }

    [Fact]
    public void UnknownSourceAndType_AreValidOpenSetValues()
    {
        var plan = Sample() with
        {
            Items =
            [
                new PlanItem { Id = "x1", Type = "somatic-drift", Policy = ExpressionPolicy.background_only,
                    Text = "A subsystem nobody has imagined yet reports mild static.", Source = "dream-journal" },
            ],
        };
        var report = PlanV3Codec.Parse(PlanV3Codec.ToJson(plan));
        Assert.Empty(report.RejectedItems);
        Assert.Equal("dream-journal", report.Plan.Items.Single().Source);
    }

    [Fact]
    public void ExtensionsNeverReachTheModelFacingSerialization()
    {
        var compact = PlanV3Codec.CompactV3(Sample());
        Assert.DoesNotContain("dream-journal", compact);
        Assert.DoesNotContain("entries", compact);
    }

    [Fact]
    public void CompactV3_IsDeterministic_AndSectionsByPolicy()
    {
        var a = PlanV3Codec.CompactV3(Sample());
        var b = PlanV3Codec.CompactV3(Sample());
        Assert.Equal(a, b);
        Assert.Equal(PlanV3Codec.PlanHash(Sample()), PlanV3Codec.PlanHash(Sample()));
        Assert.Contains("[plan/3]", a);
        Assert.Contains("SAY", a);
        Assert.Contains("NEVER", a);
        Assert.Contains("BACKGROUND", a);
        Assert.Contains("[s1 superseded] The workshop is on Tuesday.", a);
        Assert.True(a.IndexOf("SAY", StringComparison.Ordinal) < a.IndexOf("NEVER", StringComparison.Ordinal));
    }

    [Fact]
    public void CoachingLint_RejectsInstructionFusedIntoFacts()
    {
        var plan = Sample() with
        {
            Items =
            [
                new PlanItem { Id = "i1", Type = "interpretation", Policy = ExpressionPolicy.must_express,
                    Text = "The tile debate left Ava in arguing form. Own it honestly.",
                    Source = "working-context" },
            ],
        };
        var ex = Assert.Throws<InvalidOperationException>(() => PlanV3Codec.CompactV3(plan));
        Assert.Contains("own it", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The same words as third-person FACT (no imperative) are fine.
        var facts = Sample() with
        {
            Items =
            [
                new PlanItem { Id = "i1", Type = "interpretation", Policy = ExpressionPolicy.must_express,
                    Text = "The afternoon's tile debate left Ava's register sharper than usual.",
                    Source = "working-context" },
            ],
        };
        _ = PlanV3Codec.CompactV3(facts);
    }

    // ---- the migration hinge -----------------------------------------------------------

    private static ResponsePlan RealisticV2() => new()
    {
        TraceId = Guid.Parse("99999999-8888-7777-6666-555555555555"),
        Act = TurnIntent.AnswerQuestion,
        Acknowledgments = [new Acknowledgment(AckKind.CorrectionAccepted, ErrorOwner.Companion,
            "It's the twenty-eighth, not the eighteenth.")],
        Content =
        [
            new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                "You said the eighteenth; Scott corrected you: the twenty-eighth.", "working-context"),
            new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                "Scott's road bike needed a new shifter cable.", "active"),
            new PlannedContent(ContentKind.Memory, ContentRequirement.MustNotContradict,
                "The party was originally planned for the eighteenth.", "superseded"),
        ],
        Epistemic = [new EpistemicNote(EpistemicKind.NotLearned, "quokka")],
        Question = new PlannedQuestion(QuestionKind.Curiosity, "Who else is coming?", Mandatory: false),
        Tone = new ToneGuidance("short and casual", "good spirits", "warm, quick"),
    };

    [Fact]
    public void V2ToV3ToV2_ReproducesByteIdenticalCompactV2()
    {
        var v2 = RealisticV2();
        var back = V2Translation.ToV2(V2Translation.FromV2(v2));
        Assert.Equal(PlanSerialization.CompactV2(v2), PlanSerialization.CompactV2(back));
    }

    [Fact]
    public void V3ToV2_DropsBackgroundOnly_RatherThanDemotingIt()
    {
        var v3 = Sample();
        var v2 = V2Translation.ToV2(v3);
        Assert.DoesNotContain(v2.Content, c => c.Text.Contains("Rain streaks"));
        // And the may_express palette item survives as MayUse.
        Assert.Contains(v2.Content, c => c.Requirement == ContentRequirement.MayUse
                                          && c.Text.Contains("repainting"));
    }

    [Fact]
    public void QuokkaCase_TranslatesToAdmitUnknown_AndSerializesIntoNever()
    {
        var v3 = V2Translation.FromV2(RealisticV2());
        var item = v3.Items.Single(i => i.Type == "knowledge-boundary");
        Assert.Equal(ExpressionPolicy.admit_unknown, item.Policy);
        Assert.Contains("[" + item.Id + " knowledge-boundary] quokka", PlanV3Codec.CompactV3(v3));
    }
}
