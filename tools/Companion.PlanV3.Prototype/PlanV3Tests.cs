using System.Text;
using System.Text.Json.Nodes;
using Companion.Core.Domain;
using Companion.RendererBench;
using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// Proof obligations for spec revision 2: stable principal identity, the two hashes with
/// volatile redaction and keyed correlation, protected v2 fallback, six-policy model,
/// evidence-backed restriction authority, wire consistency — plus everything rev-1 proved.
/// </summary>
public class PlanV3Tests
{
    private static readonly Participant User = new("usr-local", ParticipantRole.user, "Scott");
    private static readonly Participant Ava = new("companion-ava", ParticipantRole.companion, "Ava");

    private static PlanItem Item(string id, string type, ExpressionPolicy policy, string text,
        string source = "retrieval", string? reason = null, bool quoted = false,
        Provenance? prov = null) => new()
    {
        Id = id, Type = type, Policy = policy, Text = text, Source = source,
        ReasonCode = reason, Quoted = quoted, Provenance = prov,
    };

    private static PlanV3 Sample() => new()
    {
        TraceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Participants = [User, Ava],
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

    /// <summary>The corrected grief case: information about Scott's FATHER, supplied by
    /// Scott, owned by a third party not present, audience restricted to Scott.</summary>
    private static PlanV3 GriefPlan() => new()
    {
        TraceId = Guid.NewGuid(),
        Participants = [User, Ava],
        Act = "acknowledge",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Items =
        [
            Item("g1", "acknowledgment", ExpressionPolicy.must_express,
                "Scott's father's scan results come back tomorrow.", "working-context",
                prov: new Provenance(Origin: "told-by-user")) with
            {
                Classification = Classification.@private,
                Disclosure = Disclosure.restricted,
                Owner = "principal:scott-father",
                Audience = ["usr-local"],
                Retention = Retention.volatile_turn_only,
            },
        ],
        Register = new RegisterVector { Warmth = "tender", Verbosity = "short" },
    };

    // ---- rev-2 §1: stable identity ------------------------------------------------------

    [Fact]
    public void RestrictedDisclosure_RequiresResolvableAudience_AndOwnersMayBeAbsentThirdParties()
    {
        var grief = GriefPlan();
        Assert.Empty(PlanV3Codec.Validate(grief));

        var badAudience = grief with
        {
            Items = [grief.Items[0] with { Audience = ["Scott"] }], // display name, not an id
        };
        Assert.Contains(PlanV3Codec.Validate(badAudience),
            e => e.Contains("neither an in-plan participant id nor a scheme-prefixed principal ref"));

        var noAudience = grief with { Items = [grief.Items[0] with { Audience = null }] };
        Assert.Contains(PlanV3Codec.Validate(noAudience), e => e.Contains("requires an explicit audience"));

        var badOwner = grief with { Items = [grief.Items[0] with { Owner = "Scott's dad" }] };
        Assert.Contains(PlanV3Codec.Validate(badOwner), e => e.Contains("owner"));
    }

    [Fact]
    public void Participants_MustIncludeUserAndCompanion_WithUniqueIds()
    {
        var dup = Sample() with { Participants = [User, User with { Role = ParticipantRole.companion }] };
        Assert.Contains(PlanV3Codec.Validate(dup), e => e.Contains("duplicate participant ids"));

        var missing = Sample() with { Participants = [User] };
        Assert.Contains(PlanV3Codec.Validate(missing), e => e.Contains("user and a companion"));
    }

    // ---- rev-2 §2: two hashes, canonical wire form --------------------------------------

    [Fact]
    public void WireHash_SeesExtensions_RenderHashDoesNot()
    {
        var a = Sample();
        var b = a with { Extensions = (JsonObject?)JsonNode.Parse("{\"dream-journal\":{\"entries\":4}}") };

        Assert.NotEqual(PlanV3Codec.WirePlanHash(a), PlanV3Codec.WirePlanHash(b));
        Assert.Equal(PlanV3Codec.RenderPromptHash(a), PlanV3Codec.RenderPromptHash(b));
    }

    [Fact]
    public void CanonicalJson_IsOrderInsensitive_SoTheWireHashIsCross_ProducerStable()
    {
        var doc1 = "{\"b\":1,\"a\":{\"y\":2,\"x\":3},\"arr\":[1,2]}";
        var doc2 = "{\"arr\":[1,2],\"a\":{\"x\":3,\"y\":2},\"b\":1}";
        Assert.Equal(
            PlanV3Codec.CanonicalJson(JsonNode.Parse(doc1)),
            PlanV3Codec.CanonicalJson(JsonNode.Parse(doc2)));
    }

    // ---- rev-2 §3: volatile content never yields plain content-derived identifiers ------

    [Fact]
    public void WireHash_RedactsVolatileText_SoDictionaryAttacksLearnNothing()
    {
        var grief = GriefPlan();
        var sibling = grief with
        {
            Items = [grief.Items[0] with { Text = "Scott's father's scan results were clean." }],
        };
        // Different private texts, identical wire hashes: the hash derives nothing from them.
        Assert.Equal(PlanV3Codec.WirePlanHash(grief), PlanV3Codec.WirePlanHash(sibling));
        Assert.True(PlanV3Codec.ContainsVolatile(grief));
    }

    [Fact]
    public void CorrelationTag_IsKeyed_AndVersioned()
    {
        var grief = GriefPlan();
        var k1 = Encoding.UTF8.GetBytes("deployment-secret-one");
        var k2 = Encoding.UTF8.GetBytes("deployment-secret-two");

        var tag1 = PlanV3Codec.CorrelationTag(grief, k1, 1);
        Assert.StartsWith("v1:", tag1);
        Assert.Equal(tag1, PlanV3Codec.CorrelationTag(grief, k1, 1));
        Assert.NotEqual(tag1[3..], PlanV3Codec.CorrelationTag(grief, k2, 2)[3..]);
    }

    // ---- rev-2 §4: six policies, single authorities -------------------------------------

    [Fact]
    public void QuestionProhibitionAndStyle_HaveExactlyOneOwnerEach()
    {
        Assert.DoesNotContain("question_forbidden", Enum.GetNames<ExpressionPolicy>());
        Assert.DoesNotContain("style_guidance", Enum.GetNames<ExpressionPolicy>());
        Assert.Equal(6, Enum.GetNames<ExpressionPolicy>().Length);
    }

    // ---- rev-2 §5: protected fallback ---------------------------------------------------

    [Fact]
    public void PrivateVolatileMustExpress_CannotFallThroughLossyV2()
    {
        var compat = PlanV3Codec.CheckV2Compatibility(GriefPlan());
        Assert.False(compat.Compatible);
        Assert.Contains(compat.Reasons, r => r.Contains("retention"));
        Assert.Contains(compat.Reasons, r => r.Contains("restricted disclosure"));
        Assert.Throws<InvalidOperationException>(() => V2Translation.TranslateToV2(GriefPlan()));
    }

    [Fact]
    public void HarmlessBackgroundAdditions_TranslateSafely()
    {
        var v3 = V2Translation.FromV2(RealisticV2()) with { };
        var withBackground = v3 with
        {
            Items = [.. v3.Items,
                Item("v9", "observation", ExpressionPolicy.background_only,
                    "Rain streaks the window.", "vision")],
        };
        Assert.True(PlanV3Codec.CheckV2Compatibility(withBackground).Compatible);
        var v2 = V2Translation.TranslateToV2(withBackground);
        Assert.DoesNotContain(v2.Content, c => c.Text.Contains("Rain streaks"));
    }

    [Fact]
    public void InvalidV3_IsNeverAutomaticallyV2Compatible()
    {
        var invalid = Sample() with { Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "ghost") };
        var compat = PlanV3Codec.CheckV2Compatibility(invalid);
        Assert.False(compat.Compatible);
        Assert.Contains(compat.Reasons, r => r.Contains("invalid v3 does not imply v2 is semantically safe"));
    }

    // ---- rev-2 §6: traceable authority + wire consistency -------------------------------

    [Fact]
    public void UserPreferenceRestrictions_RequireEvidence_NotMereClaims()
    {
        var claimed = Sample() with
        {
            Register = Sample().Register with { Profanity = "forbidden" },
            RegisterRestrictions =
            [
                new RegisterRestriction("profanity", "forbidden", "user-config",
                    "user-preference.no-profanity-standing-rule"),
            ],
        };
        Assert.Contains(PlanV3Codec.Validate(claimed), e => e.Contains("authority cannot merely be claimed"));

        var evidenced = claimed with
        {
            RegisterRestrictions =
            [
                new RegisterRestriction("profanity", "forbidden", "user-config",
                    "user-preference.no-profanity-standing-rule",
                    new Provenance(Origin: "told-by-user", EvidenceRef: "preference:1734")),
            ],
        };
        Assert.Empty(PlanV3Codec.Validate(evidenced));
    }

    [Fact]
    public void RegisterRestrictions_DimensionsAndValues_AreClosedAndValidated()
    {
        var bad = Sample() with
        {
            RegisterRestrictions = [new RegisterRestriction("charisma", "eleven", "x",
                "user-preference.x", new Provenance(EvidenceRef: "preference:1"))],
        };
        var errors = PlanV3Codec.Validate(bad);
        Assert.Contains(errors, e => e.Contains("unknown dimension"));

        var badValue = Sample() with
        {
            RegisterRestrictions = [new RegisterRestriction("warmth", "molten", "x",
                "user-preference.x", new Provenance(EvidenceRef: "preference:1"))],
        };
        Assert.Contains(PlanV3Codec.Validate(badValue), e => e.Contains("not a legal value"));
    }

    [Fact]
    public void LegacyStyle_IsMigrationMetadataOnly_NeverInCompactV3()
    {
        var v3 = V2Translation.FromV2(RealisticV2());
        Assert.Contains("short and casual", v3.Register.LegacyStyle);
        Assert.DoesNotContain("short and casual", PlanV3Codec.CompactV3(v3));
    }

    [Fact]
    public void WireEnumsAreSnakeCase_CompactLabelsAreKebab()
    {
        var json = PlanV3Codec.ToJson(Sample() with
        {
            Items = [Sample().Items[0] with { Category = RenderCategory.shared_memory }],
            Question = new QuestionPolicyBlock(QuestionPolicy.may_ask),
        });
        Assert.Contains("\"shared_memory\"", json);
        var compact = PlanV3Codec.CompactV3(Sample() with
        {
            Items = [Sample().Items[4] with { Category = RenderCategory.shared_memory }],
            Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        });
        Assert.Contains("shared-memory", compact);
        Assert.DoesNotContain("shared_memory", compact);
    }

    // ---- carried over from rev-1 --------------------------------------------------------

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
    public void InvalidPlans_NeverReachTheSerializer()
    {
        var broken = Sample() with { Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "nope") };
        Assert.Throws<InvalidOperationException>(() => PlanV3Codec.CompactV3(broken));
    }

    [Fact]
    public void Extensions_AreSemanticallyPreserved_AndNeverModelFacing()
    {
        var raw = PlanV3Codec.ToJson(Sample()).Replace(
            "{\"dream-journal\":{\"entries\":3}}",
            "{  \"dream-journal\" : { \"entries\" :   3 } }");
        var report = PlanV3Codec.Parse(raw);
        Assert.True(report.Valid);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("{\"dream-journal\":{\"entries\":3}}"), report.ValidPlan.Extensions));
        Assert.DoesNotContain("dream-journal", PlanV3Codec.CompactV3(report.ValidPlan));
    }

    [Fact]
    public void OpenSemanticTypes_NeverAppearInCompactV3()
    {
        var compact = PlanV3Codec.CompactV3(Sample());
        Assert.DoesNotContain("somatic-drift", compact);
        Assert.Contains("[v1 observation]", compact);
    }

    [Fact]
    public void CoachingLint_IsProvenanceAware()
    {
        Assert.NotNull(PlanV3Codec.CoachingViolation(
            Item("i1", "interpretation", ExpressionPolicy.must_express,
                "The tile debate left Ava in arguing form. Own it honestly.", "working-context")));
        Assert.Null(PlanV3Codec.CoachingViolation(
            Item("m9", "memory", ExpressionPolicy.may_express,
                "Scott's note on the fridge says: make sure to water the ferns.", "retrieval")));
        Assert.Null(PlanV3Codec.CoachingViolation(
            Item("u1", "interpretation", ExpressionPolicy.must_express,
                "Scott said: \"be honest with me about the timeline.\"", "working-context",
                quoted: true, prov: new Provenance(Origin: "told-by-user"))));
    }

    [Fact]
    public void StructuralInvariants_StillHold()
    {
        Assert.Empty(PlanV3Codec.Validate(Sample()));
        var overBudget = Sample() with { Budget = new Budget(MaxItems: 2) };
        Assert.Contains(PlanV3Codec.Validate(overBudget), e => e.Contains("over-budget"));
        Assert.Equal(PlanV3Codec.RenderPromptHash(Sample()), PlanV3Codec.RenderPromptHash(Sample()));
    }

    [Fact]
    public void GriefIsStillSaid_RetentionDoesNotGagExpression()
    {
        Assert.Contains("scan results come back tomorrow", PlanV3Codec.CompactV3(GriefPlan()));
    }

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
    public void V2ToV3ToV2_StillReproducesByteIdenticalCompactV2()
    {
        var v2 = RealisticV2();
        var v3 = V2Translation.FromV2(v2);
        Assert.Empty(PlanV3Codec.Validate(v3));
        Assert.True(PlanV3Codec.CheckV2Compatibility(v3).Compatible);
        Assert.Equal(PlanSerialization.CompactV2(v2),
                     PlanSerialization.CompactV2(V2Translation.TranslateToV2(v3)));
    }
}
