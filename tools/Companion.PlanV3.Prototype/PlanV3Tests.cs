using System.Text.Json.Nodes;
using Companion.Core.Domain;
using Companion.RendererBench;
using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// Proof obligations for spec rev-1: whole-plan invalidation on unknown closed-set values,
/// semantic (not byte) extension preservation, provenance-aware coaching lint, closed
/// model-facing categories, structural invariants, owned restrictions, the
/// disclosure/retention/expression separation, and the v2↔v3 migration hinge.
/// </summary>
public class PlanV3Tests
{
    private static PlanItem Item(string id, string type, ExpressionPolicy policy, string text,
        string source = "retrieval", string? reason = null, bool quoted = false,
        Provenance? prov = null, int? priority = null) => new()
    {
        Id = id, Type = type, Policy = policy, Text = text, Source = source,
        ReasonCode = reason, Quoted = quoted, Provenance = prov, Priority = priority,
    };

    private static PlanV3 Sample() => new()
    {
        TraceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Participants = new Participants("Scott", "Ava"),
        Act = "accept-correction",
        Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "q1"),
        Items =
        [
            Item("c1", "correction", ExpressionPolicy.must_express,
                "Ava said the workshop was Tuesday; it is Thursday.", "working-context") with
                { Value = JsonNode.Parse("{\"owner\":\"self\"}"), Supersedes = ["memory:4821"] },
            Item("s1", "superseded", ExpressionPolicy.must_not_express,
                "The workshop is on Tuesday.", "supersession",
                reason: "epistemic-integrity.superseded-or-disputed") with { SupersededBy = "c1" },
            Item("q1", "clarify", ExpressionPolicy.ask_required,
                "which list is meant — groceries or hardware", "working-context"),
            Item("v1", "somatic-drift", ExpressionPolicy.background_only,
                "Rain streaks the window behind Scott.", "vision") with { Confidence = 0.82 },
            Item("m1", "memory", ExpressionPolicy.may_express,
                "Scott is repainting the office a color he likes.", "retrieval"),
        ],
        Register = new RegisterVector { Warmth = "warm", Verbosity = "terse" },
        Extensions = (JsonObject?)JsonNode.Parse("{\"dream-journal\":{\"entries\":3}}"),
    };

    // ---- §4.3: unknown closed-set values invalidate the WHOLE plan ----------------------

    [Fact]
    public void UnknownPolicy_InvalidatesTheWholePlan_NothingIsHonored()
    {
        var json = PlanV3Codec.ToJson(Sample())
            .Replace("\"policy\":\"may_express\"", "\"policy\":\"must_whisper\"");

        var report = PlanV3Codec.Parse(json);

        Assert.False(report.Valid);
        Assert.Contains(report.Errors, e => e.Contains("must_whisper") && e.Contains("whole plan invalid"));
        Assert.Throws<InvalidOperationException>(() => report.ValidPlan);
    }

    [Fact]
    public void UnknownQuestionPolicy_AlsoInvalidatesTheWholePlan()
    {
        var json = PlanV3Codec.ToJson(Sample())
            .Replace("\"policy\":\"ask_required\",\"itemId\":\"q1\"", "\"policy\":\"beg\",\"itemId\":\"q1\"");
        Assert.False(PlanV3Codec.Parse(json).Valid);
    }

    [Fact]
    public void InvalidPlans_NeverReachTheSerializer()
    {
        var broken = Sample() with { Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "nope") };
        Assert.Throws<InvalidOperationException>(() => PlanV3Codec.CompactV3(broken));
    }

    // ---- §4.4: extensions are SEMANTICALLY preserved (canonical re-emit, not bytes) -----

    [Fact]
    public void Extensions_AreSemanticallyPreserved_AcrossOddFormattingAndParse()
    {
        var raw = PlanV3Codec.ToJson(Sample()).Replace(
            "{\"dream-journal\":{\"entries\":3}}",
            "{  \"dream-journal\" : { \"entries\" :   3 } }");
        var report = PlanV3Codec.Parse(raw);

        Assert.True(report.Valid);
        Assert.Equal(["dream-journal"], report.UnknownExtensionBlocks);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("{\"dream-journal\":{\"entries\":3}}"),
            report.ValidPlan.Extensions));
        // And they never reach the model-facing form.
        var compact = PlanV3Codec.CompactV3(report.ValidPlan);
        Assert.DoesNotContain("dream-journal", compact);
        Assert.DoesNotContain("entries", compact);
    }

    // ---- §10: open type never reaches the prompt; closed categories do ------------------

    [Fact]
    public void OpenSemanticTypes_NeverAppearInCompactV3_ClosedCategoriesDo()
    {
        var compact = PlanV3Codec.CompactV3(Sample());
        Assert.DoesNotContain("somatic-drift", compact);      // open type, diagnostics-only
        Assert.Contains("[v1 observation]", compact);          // closed rendering category
        Assert.Contains("[s1 superseded]", compact);
        Assert.Contains("[q1 clarify]", compact);
    }

    // ---- §2.4: provenance-aware coaching lint -------------------------------------------

    [Fact]
    public void CoachingLint_RejectsProducerAuthoredCoaching_Only()
    {
        var authored = Item("i1", "interpretation", ExpressionPolicy.must_express,
            "The tile debate left Ava in arguing form. Own it honestly.", "working-context");
        Assert.NotNull(PlanV3Codec.CoachingViolation(authored));

        // The same imperative inside a MEMORY is a fact about the world, not coaching.
        var memory = Item("m9", "memory", ExpressionPolicy.may_express,
            "Scott's note on the fridge says: make sure to water the ferns.", "retrieval");
        Assert.Null(PlanV3Codec.CoachingViolation(memory));

        // Quoted user speech is exempt even from an authored source, provenance-gated.
        var quoted = Item("u1", "interpretation", ExpressionPolicy.must_express,
            "Scott said: \"be honest with me about the timeline.\"", "working-context",
            quoted: true, prov: new Provenance(Origin: "told-by-user"));
        Assert.Null(PlanV3Codec.CoachingViolation(quoted));

        // Third-person fact with no imperative passes from any source.
        var fact = Item("i2", "interpretation", ExpressionPolicy.must_express,
            "The afternoon's tile debate left Ava's register sharper than usual.", "working-context");
        Assert.Null(PlanV3Codec.CoachingViolation(fact));
    }

    [Fact]
    public void Quoted_WithoutQuoteCapableProvenance_IsAValidationError()
    {
        var plan = Sample() with
        {
            Items = [.. Sample().Items,
                Item("x1", "interpretation", ExpressionPolicy.must_express, "whatever",
                    "working-context", quoted: true)],
        };
        Assert.Contains(PlanV3Codec.Validate(plan), e => e.Contains("quoted requires provenance"));
    }

    // ---- §9: structural invariants ------------------------------------------------------

    [Fact]
    public void StructuralInvariants_CatchTheWholeReviewList()
    {
        var basePlan = Sample();

        var dup = basePlan with { Items = [.. basePlan.Items, basePlan.Items[0]] };
        Assert.Contains(PlanV3Codec.Validate(dup), e => e.Contains("duplicate item id"));

        var empty = basePlan with { Items = [.. basePlan.Items,
            new PlanItem { Id = "z1", Type = "claim", Policy = ExpressionPolicy.must_express, Source = "retrieval" }] };
        Assert.Contains(PlanV3Codec.Validate(empty), e => e.Contains("requires text or value"));

        var unowned = basePlan with { Items = [.. basePlan.Items,
            Item("z2", "secret", ExpressionPolicy.must_not_express, "hush", "somewhere")] };
        Assert.Contains(PlanV3Codec.Validate(unowned), e => e.Contains("reasonCode"));

        var danglingSup = basePlan with { Items = [.. basePlan.Items,
            Item("z3", "claim", ExpressionPolicy.must_express, "x", "retrieval") with { Supersedes = ["ghost9"] }] };
        Assert.Contains(PlanV3Codec.Validate(danglingSup), e => e.Contains("neither an in-plan item nor an external"));

        var qForbiddenWithAsk = basePlan with
        { Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden) };
        Assert.Contains(PlanV3Codec.Validate(qForbiddenWithAsk), e => e.Contains("contains an ask_required item"));

        var overBudget = basePlan with { Budget = new Budget(MaxItems: 2) };
        Assert.Contains(PlanV3Codec.Validate(overBudget), e => e.Contains("over-budget"));

        Assert.Empty(PlanV3Codec.Validate(basePlan));
    }

    [Fact]
    public void RegisterDefaults_AreCanonicalAndDeterministic()
    {
        var c = PlanV3Codec.Canonicalize(new RegisterVector());
        Assert.Equal("plain", c.Warmth);
        Assert.Equal("off", c.Playfulness);
        Assert.Equal("neutral", c.Profanity);
        Assert.Equal(PlanV3Codec.CompactV3(Sample()), PlanV3Codec.CompactV3(Sample()));
        Assert.Equal(PlanV3Codec.PlanHash(Sample()), PlanV3Codec.PlanHash(Sample()));
    }

    // ---- §1: restrictions must be owned -------------------------------------------------

    [Fact]
    public void RestrictiveProfanity_RequiresAnOwnedTraceableRestriction()
    {
        var unowned = Sample() with { Register = Sample().Register with { Profanity = "forbidden" } };
        Assert.Contains(PlanV3Codec.Validate(unowned), e => e.Contains("registerRestrictions"));

        var owned = unowned with
        {
            RegisterRestrictions =
            [
                new RegisterRestriction("profanity", "forbidden", "user-config",
                    "user-preference.no-profanity-standing-rule"),
            ],
        };
        Assert.Empty(PlanV3Codec.Validate(owned));
    }

    // ---- §2: disclosure/retention/expression are independent ----------------------------

    [Fact]
    public void PrivateVolatileContent_CanStillBeMustExpress()
    {
        var grief = Sample() with
        {
            Act = "acknowledge",
            Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
            Items =
            [
                Item("g1", "acknowledgment", ExpressionPolicy.must_express,
                    "Scott's father's scan results come back tomorrow.", "working-context",
                    prov: new Provenance(Origin: "told-by-user")) with
                {
                    Classification = Classification.@private,
                    Disclosure = Disclosure.owner_only,
                    Retention = Retention.volatile_turn_only,
                },
            ],
        };
        Assert.Empty(PlanV3Codec.Validate(grief));
        // Expression is untouched by retention: the item serializes into SAY.
        Assert.Contains("scan results come back tomorrow", PlanV3Codec.CompactV3(grief));
    }

    // ---- §8: the migration hinge --------------------------------------------------------

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
        var v3 = V2Translation.FromV2(v2);
        Assert.Empty(PlanV3Codec.Validate(v3));
        var back = V2Translation.ToV2(v3);
        Assert.Equal(PlanSerialization.CompactV2(v2), PlanSerialization.CompactV2(back));
    }

    [Fact]
    public void V3ToV2_DropsBackgroundOnly_RatherThanDemotingIt()
    {
        var v2 = V2Translation.ToV2(Sample());
        Assert.DoesNotContain(v2.Content, c => c.Text.Contains("Rain streaks"));
        Assert.Contains(v2.Content, c => c.Requirement == ContentRequirement.MayUse
                                          && c.Text.Contains("repainting"));
    }

    [Fact]
    public void QuokkaCase_TranslatesToAdmitUnknown_AndRendersAsBoundary()
    {
        var v3 = V2Translation.FromV2(RealisticV2());
        var item = v3.Items.Single(i => i.Type == "knowledge-boundary");
        Assert.Equal(ExpressionPolicy.admit_unknown, item.Policy);
        Assert.Contains($"[{item.Id} boundary] quokka", PlanV3Codec.CompactV3(v3));
    }

    // ---- §3: procedure state, corrected ownership ---------------------------------------

    [Fact]
    public void TwentyQuestionsPlan_CarriesSelectedQuestionAndMinimalBackground()
    {
        // The PROCEDURE chose question 16 upstream; the mouth gets the selected question
        // plus only the constraints needed to render it faithfully — not the whole ledger.
        var plan = new PlanV3
        {
            TraceId = Guid.NewGuid(),
            Participants = new Participants("Scott", "Ava"),
            Act = "answer-question",
            Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "q1"),
            Items =
            [
                Item("q1", "activity-question", ExpressionPolicy.ask_required,
                    "is the object made mostly of metal", "procedure"),
                Item("b1", "activity-state", ExpressionPolicy.background_only,
                    "Twenty Questions: Ava asks; question 16 of 20 is next.", "procedure"),
            ],
        };
        Assert.Empty(PlanV3Codec.Validate(plan));
        var compact = PlanV3Codec.CompactV3(plan);
        Assert.Contains("[q1 clarify] is the object made mostly of metal", compact);
        Assert.Contains("BACKGROUND", compact);
    }
}
