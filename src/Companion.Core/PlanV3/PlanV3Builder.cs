using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.PlanV3;

/// <summary>
/// The NATIVE v3 builder (P4, docs/RESPONSE_PLAN_V3_SPEC.md §15): constructs a PlanV3
/// directly from upstream cognitive state — the same typed inputs ResponsePlanner.Build
/// consumes — with no V2 plan, V2 serialization, acknowledgment template, or fused
/// coaching prose anywhere in its diet. `planOrigin = native_v3` is legal only for this
/// builder's output, and <see cref="NativeBuildResult.Provenance"/> names every input it
/// actually consumed so accidental V2 ancestry is auditable (plus test-enforced: no V2
/// template phrase may appear in native item text).
///
/// Facts and instructions separate at the source: acknowledgments become typed items
/// whose text is the USER'S OWN WORDS (quoted, told-by-user) plus typed owner values —
/// never the "Ava made an error; Scott corrected her" template. The source-side coaching
/// lint runs at item creation: a producer-authored fact hiding an imperative is rejected
/// with a content-safe diagnostic, and the build continues without it.
/// </summary>
public static class PlanV3Builder
{
    public sealed record NativeBuildResult(
        PlanV3 Plan,
        IReadOnlyList<string> LintRejections,   // content-safe: "itemId source rule"
        IReadOnlyList<string> Provenance);      // upstream inputs consumed, by name

    public static NativeBuildResult Build(
        Guid traceId,
        TurnIntentState intent,
        WorkingContextState working,
        string userMessage,
        IReadOnlyList<RetrievalResult> retrieved,
        ConceptLookupResult? knowledge,
        string? curiosityQuestion,
        bool sensitiveTurn,
        string userParticipantId,
        string userDisplay,
        string companionParticipantId,
        string companionDisplay)
    {
        var items = new List<PlanItem>();
        var lintRejections = new List<string>();
        var provenance = new List<string> { "intent.Intent", "working.Move" };
        var n = 0;
        string NextId(string prefix) => $"{prefix}{++n}";

        // Sensitive turns: recording is already gated upstream, and the items themselves
        // carry the restriction so the protection is intrinsic, not positional.
        var retention = sensitiveTurn ? Retention.no_telemetry_text : Retention.full;

        void Add(PlanItem item)
        {
            if (PlanV3Codec.CoachingViolation(item) is { } violation)
            {
                // Content-safe: source, id, rule family — never the text.
                lintRejections.Add($"{item.Id} source={item.Source} rule=producer-coaching");
                return;
            }
            items.Add(item);
        }

        // ---- acknowledgments: typed moves + the user's own quoted words ----------------
        if (working.Move == ConversationMove.Correction)
        {
            provenance.Add("working.CorrectionTarget");
            var owner = working.CorrectionTarget switch
            {
                ErrorOwner.Companion => companionParticipantId,
                ErrorOwner.User => userParticipantId,
                _ => "nobody",
            };
            Add(new PlanItem
            {
                Id = NextId("a"),
                Type = "correction",
                Category = RenderCategory.correction,
                Policy = ExpressionPolicy.must_express,
                Text = Clip(userMessage),
                Quoted = true,
                Provenance = new Provenance(Origin: "told-by-user"),
                Value = System.Text.Json.Nodes.JsonNode.Parse(
                    $"{{\"owner\":{System.Text.Json.JsonSerializer.Serialize(owner)}}}"),
                Source = "working-context",
                Retention = retention,
            });
        }
        if (working.Move == ConversationMove.ConfirmsClaim)
        {
            Add(new PlanItem
            {
                Id = NextId("a"),
                Type = "agreement",
                Category = RenderCategory.agreement,
                Policy = ExpressionPolicy.must_express,
                Text = Clip(userMessage),
                Quoted = true,
                Provenance = new Provenance(Origin: "told-by-user"),
                Value = System.Text.Json.Nodes.JsonNode.Parse("{\"owner\":\"nobody\"}"),
                Source = "working-context",
                Retention = retention,
            });
        }
        if (working is { Move: ConversationMove.AnswersOpenQuestion, BoundQuestion: { } bound })
        {
            provenance.Add("working.BoundQuestion");
            Add(new PlanItem
            {
                Id = NextId("a"),
                Type = "answer-received",
                Category = RenderCategory.answer,
                Policy = ExpressionPolicy.must_express,
                Text = Clip(bound),
                Quoted = true,
                Provenance = new Provenance(Origin: "told-by-user"),
                Source = "working-context",
                Retention = retention,
            });
        }
        if (TeachingDetector.Detect(userMessage) is { } teaching)
        {
            provenance.Add("TeachingDetector(userMessage)");
            Add(new PlanItem
            {
                Id = NextId("a"),
                Type = "teaching",
                Category = RenderCategory.teaching,
                Policy = ExpressionPolicy.must_express,
                Text = Clip(teaching.Sentence),
                Quoted = true,
                Provenance = new Provenance(Origin: "told-by-user"),
                Source = "working-context",
                Retention = retention,
            });
        }

        // ---- interpretation: producer-AUTHORED, so the source-side lint applies --------
        if (working.InterpretationNote is { } interpretation)
        {
            provenance.Add("working.InterpretationNote");
            Add(new PlanItem
            {
                Id = NextId("i"),
                Type = "interpretation",
                Category = RenderCategory.claim,
                Policy = ExpressionPolicy.must_express,
                Text = Clip(interpretation),
                Source = "working-context",
                Retention = retention,
            });
        }

        // ---- retrieval: typed status/owner decide policy; tombstones carry reasons -----
        provenance.Add("retrieved[*].Memory.{Content,Status,Owner}");
        foreach (var result in retrieved.Take(8))
        {
            var memory = result.Memory;
            var stale = memory.Status is MemoryStatus.Disputed or MemoryStatus.Superseded;
            Add(new PlanItem
            {
                Id = NextId("m"),
                Type = memory switch
                {
                    ConceptAssertion => "knowledge",
                    _ when memory.Owner == MemoryOwner.Shared => "shared-memory",
                    _ => "memory",
                },
                Category = stale ? RenderCategory.superseded
                    : memory switch
                    {
                        ConceptAssertion => RenderCategory.knowledge,
                        _ when memory.Owner == MemoryOwner.Shared => RenderCategory.shared_memory,
                        _ => RenderCategory.memory,
                    },
                Policy = stale ? ExpressionPolicy.must_not_express : ExpressionPolicy.may_express,
                ReasonCode = stale ? "epistemic-integrity.superseded-or-disputed" : null,
                Text = Clip(memory.Content),
                Source = "retrieval",
                Provenance = new Provenance(
                    Origin: memory.Owner == MemoryOwner.Shared ? "shared" : "derived"),
                Retention = retention,
            });
        }

        // ---- epistemic boundary: typed familiarity, no prose ---------------------------
        if (knowledge is not null)
        {
            provenance.Add("knowledge.{Term,Familiarity,Definition}");
            switch (knowledge.Familiarity)
            {
                case ConceptFamiliarity.Unknown:
                    Add(new PlanItem
                    {
                        Id = NextId("e"), Type = "knowledge-boundary",
                        Category = RenderCategory.boundary,
                        Policy = ExpressionPolicy.admit_unknown,
                        Text = knowledge.Term, Source = "concepts", Retention = retention,
                    });
                    break;
                case ConceptFamiliarity.Heard or ConceptFamiliarity.Learning or ConceptFamiliarity.Disputed:
                    Add(new PlanItem
                    {
                        Id = NextId("e"), Type = "knowledge-boundary",
                        Category = RenderCategory.boundary,
                        Policy = ExpressionPolicy.must_not_express,
                        ReasonCode = "epistemic-integrity.uncertain-or-disputed-concept",
                        Text = knowledge.Term, Source = "concepts", Retention = retention,
                    });
                    break;
                case ConceptFamiliarity.Known when knowledge.Definition is { } definition:
                    Add(new PlanItem
                    {
                        Id = NextId("e"), Type = "knowledge",
                        Category = RenderCategory.knowledge,
                        Policy = ExpressionPolicy.must_express,
                        Text = Clip(definition), Source = "concepts",
                        Provenance = new Provenance(Origin: "taught"),
                        Retention = retention,
                    });
                    break;
            }
        }

        // ---- the question: typed intent decides; curiosity is a suggestion -------------
        QuestionPolicyBlock question;
        if (intent.Intent == TurnIntent.Clarify)
        {
            provenance.Add("working.ReferenceMarkers");
            var qid = NextId("q");
            Add(new PlanItem
            {
                Id = qid, Type = "clarify", Category = RenderCategory.clarify,
                Policy = ExpressionPolicy.ask_required,
                Text = $"which \"{working.ReferenceMarkers.FirstOrDefault() ?? "reference"}\" is meant",
                Source = "working-context", Retention = retention,
            });
            question = new QuestionPolicyBlock(QuestionPolicy.ask_required, qid);
        }
        else if (curiosityQuestion is not null)
        {
            provenance.Add("curiosityQuestion");
            var qid = NextId("q");
            Add(new PlanItem
            {
                Id = qid, Type = "curiosity", Category = RenderCategory.curiosity,
                Policy = ExpressionPolicy.may_express,
                Text = Clip(curiosityQuestion),
                Source = "curiosity", Retention = retention,
            });
            question = new QuestionPolicyBlock(QuestionPolicy.may_ask, qid);
        }
        else
        {
            question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden);
        }

        // ---- register: TYPED derivations only. The v2 tone prose is deliberately NOT an
        // input (parsing prose to recover structure is banned); until upstream emits typed
        // register signals, dimensions beyond act-derived verbosity stay at canonical
        // defaults — a documented P4 finding, not a defect (§15).
        var register = PlanV3Codec.Canonicalize(new RegisterVector
        {
            Verbosity = intent.Intent switch
            {
                TurnIntent.Clarify => "short",
                TurnIntent.Acknowledge => "short",
                _ => "conversational",
            },
        });

        var plan = new PlanV3
        {
            TraceId = traceId,
            Participants =
            [
                new Participant(userParticipantId, ParticipantRole.user, userDisplay),
                new Participant(companionParticipantId, ParticipantRole.companion, companionDisplay),
            ],
            Act = intent.Intent.ToKebab(),
            Question = question,
            Items = items,
            Register = register,
        };
        return new NativeBuildResult(plan, lintRejections, provenance);
    }

    private static string Clip(string text)
        => text.Length <= 200 ? text : text[..200];
}
