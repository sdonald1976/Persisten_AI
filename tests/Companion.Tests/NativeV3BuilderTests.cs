using System.Text.Json;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// P4 acceptance evidence (docs/RESPONSE_PLAN_V3_SPEC.md §15): native_v3 plans built from
/// upstream cognitive state only — synthetic state throughout, no real conversation data.
/// The eleven required cases, plus the V2-ancestry audit.
/// </summary>
public class NativeV3BuilderTests
{
    private static TurnIntentState Intent(TurnIntent i) => new() { Intent = i };

    private static WorkingContextState Working(
        ConversationMove move = ConversationMove.NewTopic,
        ErrorOwner? correctionTarget = null,
        string? interpretation = null,
        string? boundQuestion = null,
        string[]? markers = null) => new()
    {
        Move = move,
        CorrectionTarget = correctionTarget,
        InterpretationNote = interpretation,
        BoundQuestion = boundQuestion,
        ReferenceMarkers = markers ?? [],
        RawQuery = "synthetic raw query",
        RetrievalQuery = "synthetic retrieval query",
    };

    private sealed class FakeMemory(string content, MemoryStatus status, MemoryOwner owner) : IMemory
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string UserId => "usr-synth";
        public MemoryKind Kind => MemoryKind.Semantic;
        public MemoryOwner Owner => owner;
        public string Content => content;
        public double Importance => 0.3;
        public double Confidence => 0.8;
        public MemoryStatus Status => status;
        public DateTimeOffset CreatedAt => DateTimeOffset.MinValue;
        public DateTimeOffset EffectiveAt => DateTimeOffset.MinValue;
        public string? RelatedProject => null;
        public float[]? Embedding { get; set; }
    }

    private static RetrievalResult Mem(string content, MemoryStatus status = MemoryStatus.Active)
        => new()
        {
            Memory = new FakeMemory(content, status, MemoryOwner.User),
            Score = 0.5,
            Signals = new Dictionary<string, double>(),
            Reason = "synthetic",
        };

    private static PlanV3Builder.NativeBuildResult Build(
        TurnIntentState intent, WorkingContextState working, string userMessage,
        IReadOnlyList<RetrievalResult>? retrieved = null,
        ConceptLookupResult? knowledge = null, string? curiosity = null, bool sensitive = false)
        => PlanV3Builder.Build(
            Guid.Parse("dddddddd-1111-2222-3333-444444444444"),
            intent, working, userMessage, retrieved ?? [], knowledge, curiosity, sensitive,
            "usr-synth", "SynthUser", "companion-ava", "Ava");

    /// <summary>Every English phrase the V2 ack templates or SITUATION prose could emit.
    /// Native output containing any of them means V2 ancestry leaked in — the audit.</summary>
    private static readonly string[] V2TemplatePhrases =
    [
        "made an error", "corrected her", "accepts it as her own mistake",
        "is emphatically agreeing", "Nobody made an error", "just taught",
        "answered Ava's question", "has NOT learned", "never explain",
    ];

    private static void AssertNoV2Ancestry(PlanV3.PlanV3 plan)
    {
        foreach (var item in plan.Items)
            foreach (var phrase in V2TemplatePhrases)
                Assert.DoesNotContain(phrase, item.Text ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // 1. Correction ownership without acknowledgment templates.
    [Fact]
    public void Correction_CarriesTypedOwner_AndTheUsersOwnWords_NoTemplates()
    {
        var r = Build(Intent(TurnIntent.AnswerQuestion),
            Working(ConversationMove.Correction, ErrorOwner.Companion),
            "It's the twenty-eighth, not the eighteenth.");

        var c = r.Plan.Items.Single(i => i.Type == "correction");
        Assert.Equal(ExpressionPolicy.must_express, c.Policy);
        Assert.Equal("companion-ava", c.Value!["owner"]!.GetValue<string>());
        Assert.True(c.Quoted);
        Assert.Equal("told-by-user", c.Provenance!.Origin);
        Assert.Equal("It's the twenty-eighth, not the eighteenth.", c.Text);
        AssertNoV2Ancestry(r.Plan);
        Assert.Empty(PlanV3Codec.Validate(r.Plan));
    }

    // 2. Agreement without invented contrition.
    [Fact]
    public void Agreement_HasNobodyAsOwner_AndNoApologyMachinery()
    {
        var r = Build(Intent(TurnIntent.RespondToAnswer),
            Working(ConversationMove.ConfirmsClaim),
            "EXACTLY. The will and the manual. Never both.");

        var a = r.Plan.Items.Single(i => i.Type == "agreement");
        Assert.Equal("nobody", a.Value!["owner"]!.GetValue<string>());
        Assert.True(a.Quoted);
        AssertNoV2Ancestry(r.Plan);
    }

    // 3. Unknown concepts.
    [Fact]
    public void UnknownConcept_BecomesAdmitUnknown_WithBareSubject()
    {
        var r = Build(Intent(TurnIntent.AnswerQuestion), Working(),
            "Do you know what a synthetic-quokka is?",
            knowledge: new ConceptLookupResult(ConceptFamiliarity.Unknown, "synthetic-quokka"));

        var e = r.Plan.Items.Single(i => i.Type == "knowledge-boundary");
        Assert.Equal(ExpressionPolicy.admit_unknown, e.Policy);
        Assert.Equal("synthetic-quokka", e.Text);
        AssertNoV2Ancestry(r.Plan);
    }

    // 4. Required and forbidden questions.
    [Fact]
    public void QuestionPolicy_DerivesFromTypedIntent()
    {
        var clarify = Build(Intent(TurnIntent.Clarify),
            Working(markers: ["her"]), "Tell her I said yes.");
        Assert.Equal(QuestionPolicy.ask_required, clarify.Plan.Question.Policy);
        Assert.Single(clarify.Plan.Items, i => i.Policy == ExpressionPolicy.ask_required);
        Assert.Empty(PlanV3Codec.Validate(clarify.Plan));

        var forbidden = Build(Intent(TurnIntent.Acknowledge), Working(), "Done and dusted.");
        Assert.Equal(QuestionPolicy.question_forbidden, forbidden.Plan.Question.Policy);

        var suggested = Build(Intent(TurnIntent.AnswerQuestion), Working(), "All sorted.",
            curiosity: "How did the synthetic gnome census go?");
        Assert.Equal(QuestionPolicy.may_ask, suggested.Plan.Question.Policy);
        Assert.NotNull(suggested.Plan.Question.ItemId);
        Assert.Empty(PlanV3Codec.Validate(suggested.Plan));
    }

    // 5. Multiple MustExpress items.
    [Fact]
    public void MultipleMustExpress_EachCarriesItsOwnItemIdentity()
    {
        var r = Build(Intent(TurnIntent.AnswerQuestion),
            Working(ConversationMove.Correction, ErrorOwner.Companion,
                interpretation: "The synthetic ferry times moved to the synthetic morning."),
            "No — the 8:10, not the 11:40.",
            knowledge: new ConceptLookupResult(ConceptFamiliarity.Known, "synthetic-knot",
                "A synthetic knot holds synthetic loads."));

        var must = r.Plan.Items.Where(i => i.Policy == ExpressionPolicy.must_express).ToList();
        Assert.Equal(3, must.Count);
        Assert.Equal(must.Count, must.Select(i => i.Id).Distinct().Count());
    }

    // 6. Superseded facts.
    [Fact]
    public void SupersededMemory_BecomesAnOwnedTombstone()
    {
        var r = Build(Intent(TurnIntent.AnswerQuestion), Working(), "Where does it ship now?",
            retrieved: [Mem("The synthetic parcel ships to the old synthetic address.", MemoryStatus.Superseded)]);

        var t = r.Plan.Items.Single(i => i.Policy == ExpressionPolicy.must_not_express);
        Assert.Equal("epistemic-integrity.superseded-or-disputed", t.ReasonCode);
        Assert.Equal(RenderCategory.superseded, PlanV3Codec.CategoryOf(t));
        Assert.Empty(PlanV3Codec.Validate(r.Plan));
    }

    // 7. Register derived without free-text coaching.
    [Fact]
    public void Register_IsTypedDerivationOnly_NoProseConsumed()
    {
        var r = Build(Intent(TurnIntent.Clarify), Working(markers: ["it"]), "Move it up.");
        Assert.Equal("short", r.Plan.Register.Verbosity);
        Assert.Null(r.Plan.Register.LegacyStyle);   // tone prose was never an input
        Assert.Equal("plain", r.Plan.Register.Warmth); // canonical default, not parsed prose
    }

    // 8. Quoted imperative accepted; 9. producer-authored imperative rejected.
    [Fact]
    public void SourceSideLint_AcceptsQuotedImperatives_RejectsAuthoredOnes()
    {
        // The user's own imperative arrives quoted and survives.
        var quoted = Build(Intent(TurnIntent.AnswerQuestion),
            Working(ConversationMove.Correction, ErrorOwner.User),
            "Make sure to use the synthetic south lot, I had it backwards.");
        Assert.Contains(quoted.Plan.Items, i => i.Type == "correction" && i.Quoted);
        Assert.Empty(quoted.LintRejections);

        // A producer-authored interpretation hiding coaching is rejected at CREATION,
        // with a content-safe diagnostic (id + source + rule, no text).
        var authored = Build(Intent(TurnIntent.AnswerQuestion),
            Working(interpretation: "The synthetic debate left Ava sharper. Own it honestly."),
            "How are you feeling tonight?");
        Assert.DoesNotContain(authored.Plan.Items, i => i.Type == "interpretation");
        var rejection = Assert.Single(authored.LintRejections);
        Assert.Contains("source=working-context", rejection);
        Assert.Contains("rule=producer-coaching", rejection);
        Assert.DoesNotContain("Own it honestly", rejection);
    }

    // 10. Private/volatile item safely shadowed.
    [Fact]
    public void SensitiveTurn_ProducesProtectedItems_AndARedactedEnvelope()
    {
        var r = Build(Intent(TurnIntent.Acknowledge),
            Working(interpretation: "A synthetic relative's synthetic results arrive tomorrow."),
            "The synthetic results come back tomorrow.", sensitive: true);

        Assert.All(r.Plan.Items, i => Assert.Equal(Retention.no_telemetry_text, i.Retention));
        Assert.True(PlanV3Codec.ContainsProtectedContent(r.Plan));

        var translated = V2Translation.FromV2(new ResponsePlan
        {
            Act = TurnIntent.Acknowledge,
            Tone = new ToneGuidance("short", null, null),
        });
        var env = V3ShadowEnvelopeBuilder.Build(
            new ResponsePlan { Act = TurnIntent.Acknowledge, Tone = new ToneGuidance("short", null, null) },
            translated, null, 1, ["usr-local"],
            new RendererTrustContext(RendererTransport.local_loopback));
        env = V3ShadowEnvelopeBuilder.WithNative(env, translated, r.Plan, null, r.LintRejections,
            null, 1, ["usr-synth"], new RendererTrustContext(RendererTransport.local_loopback));

        Assert.NotNull(env.Native);
        Assert.Null(env.Native!.RenderPromptHash);       // protected: content-derived hash withheld
        Assert.True(env.Native.RedactedItemCount > 0);
        Assert.DoesNotContain("results arrive tomorrow", JsonSerializer.Serialize(env));
    }

    // 11. Native-builder failure leaves the translated path untouched.
    [Fact]
    public void NativeBuildFailure_RecordsContentSafeDiagnostic_TranslatedRowUnaffected()
    {
        var v2 = new ResponsePlan
        {
            Act = TurnIntent.Acknowledge,
            Content = [new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                "The synthetic gnome census reached eleven.", "active")],
            Tone = new ToneGuidance("short", null, null),
        };
        var translated = V2Translation.FromV2(v2);
        var env = V3ShadowEnvelopeBuilder.Build(v2, translated, null, 1, ["usr-local"],
            new RendererTrustContext(RendererTransport.local_loopback));
        env = V3ShadowEnvelopeBuilder.WithNative(env, translated, native: null,
            nativeBuildError: "SyntheticException: builder exploded synthetically",
            nativeLintRejections: [], null, 1, ["usr-local"],
            new RendererTrustContext(RendererTransport.local_loopback));

        Assert.Null(env.Native);
        Assert.Contains("SyntheticException", env.NativeBuildError);
        Assert.True(env.Valid);                        // the translated side is untouched
        Assert.Equal("translated_v2", env.PlanOrigin);
    }

    // Semantic parity: shared upstream state converges; template-vs-quote diverges honestly.
    [Fact]
    public void Parity_ReportsConvergenceAndHonestDifferences_ByItemAttribution()
    {
        // A finding worth recording: at PLAN level both sides carry the user's own words
        // for acknowledgments — the V2 English template lives only in the SERIALIZER — so
        // shared upstream state converges across every class. The honest divergence comes
        // from the source-side lint: a coaching-fused interpretation survives into v2 but
        // is rejected by the native builder, and parity attributes exactly that.
        var working = Working(ConversationMove.Correction, ErrorOwner.Companion,
            interpretation: "The synthetic debate left Ava sharper. Own it honestly.");
        const string msg = "It's the synthetic twenty-eighth, not the eighteenth.";
        var retrieved = new[] { Mem("The synthetic bike needed a synthetic cable.") };

        var v2 = Core.Services.ResponsePlanner.Build(
            Guid.NewGuid(), Intent(TurnIntent.AnswerQuestion), working, msg,
            retrieved, null, null, "short", null, null);
        var translated = V2Translation.FromV2(v2);
        var native = Build(Intent(TurnIntent.AnswerQuestion), working, msg, retrieved);
        Assert.Single(native.LintRejections);

        var report = PlanParity.Compare(translated, native.Plan);
        var byClass = report.Classes.ToDictionary(c => c.Class);

        Assert.Equal("match", byClass["act"].Status);
        Assert.Equal("match", byClass["question-policy"].Status);
        Assert.Equal("match", byClass["optional-content"].Status);      // same memory text
        Assert.Equal("match", byClass["correction-ownership"].Status);  // enum vs id, normalized
        // The lint-rejected interpretation is missing from native required content —
        // evidence, attributed by item id, no text.
        Assert.Equal("native-missing", byClass["required-content"].Status);
        Assert.All(byClass["required-content"].Details,
            d => Assert.StartsWith("native-missing:", d));
        Assert.Equal("incomparable-prose", byClass["register-intent"].Status);
    }
}
