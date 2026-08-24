using Companion.Core.Domain;

namespace Companion.PlanV3;

/// <summary>
/// V2 ↔ V3 translation (docs/RESPONSE_PLAN_V3_SPEC.md §8). The migration hinge:
/// ToV2 must reproduce a ResponsePlan whose frozen CompactV2 serialization is
/// byte-identical to the original's, so a v3-producing mind can feed the run-1c
/// mouth unchanged. Tested against real corpus plans.
/// </summary>
public static class V2Translation
{
    public static PlanV3 FromV2(ResponsePlan v2, string user = "Scott", string companion = "Ava")
    {
        var items = new List<PlanItem>();
        var n = 0;
        string NextId(string prefix) => $"{prefix}{++n}";

        foreach (var a in v2.Acknowledgments)
        {
            items.Add(new PlanItem
            {
                Id = NextId("a"),
                Type = a.Kind switch
                {
                    AckKind.CorrectionAccepted => "correction",
                    AckKind.AgreementConfirmed => "agreement",
                    AckKind.FactTaught => "teaching",
                    AckKind.AnswerReceived => "answer-received",
                    _ => "acknowledgment",
                },
                Policy = ExpressionPolicy.must_express,
                Text = a.Text,
                Value = System.Text.Json.Nodes.JsonNode.Parse(
                    $"{{\"owner\":\"{a.ErrorOwner.ToString().ToLowerInvariant()}\",\"kind\":\"{a.Kind}\"}}"),
                Source = "working-context",
            });
        }

        foreach (var c in v2.Content)
        {
            items.Add(new PlanItem
            {
                Id = NextId("c"),
                Type = c.Kind.ToString().ToLowerInvariant() switch
                {
                    "sharedmemory" => "shared-memory",
                    "learnedknowledge" => "knowledge",
                    var k => k,
                },
                Policy = c.Requirement switch
                {
                    ContentRequirement.MustState => ExpressionPolicy.must_express,
                    ContentRequirement.MayUse => ExpressionPolicy.may_express,
                    _ => ExpressionPolicy.must_not_express,
                },
                Text = c.Text,
                Source = "retrieval",
                Provenance = c.Provenance is { } p ? new Provenance(Origin: p) : null,
            });
        }

        foreach (var e in v2.Epistemic)
        {
            items.Add(new PlanItem
            {
                Id = NextId("e"),
                Type = "knowledge-boundary",
                Policy = e.Kind == EpistemicKind.NotLearned
                    ? ExpressionPolicy.admit_unknown : ExpressionPolicy.must_not_express,
                Text = e.Subject,
                Value = System.Text.Json.Nodes.JsonNode.Parse(
                    $"{{\"kind\":\"{e.Kind.ToString().ToLowerInvariant()}\"}}"),
                Source = "concepts",
            });
        }

        string? questionItemId = null;
        if (v2.Question is { } q)
        {
            questionItemId = NextId("q");
            items.Add(new PlanItem
            {
                Id = questionItemId,
                Type = q.Kind == QuestionKind.Clarify ? "clarify" : "curiosity",
                Policy = q.Mandatory ? ExpressionPolicy.ask_required : ExpressionPolicy.may_express,
                Text = q.Text,
                Source = q.Kind == QuestionKind.Clarify ? "working-context" : "curiosity",
            });
        }

        return new PlanV3
        {
            TraceId = v2.TraceId,
            Participants = new Participants(user, companion),
            Act = v2.Act.ToKebab(),
            Question = new QuestionPolicyBlock(
                v2.Question is { Mandatory: true } ? QuestionPolicy.ask_required
                : v2.Question is not null ? QuestionPolicy.may_ask
                : QuestionPolicy.question_forbidden,
                questionItemId),
            Items = items,
            Register = new RegisterVector
            {
                LegacyStyle = string.Join("; ", new[]
                    { v2.Tone.Register, v2.Tone.MoodNote, v2.Tone.PersonaStyle }
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
            },
        };
    }

    /// <summary>
    /// The lossy-but-defined fallback (§8): drops provenance detail, confidence,
    /// sensitivity, validity, priority, extensions; DROPS background_only entirely
    /// (v2 has no safe carrier); reconstructs the v2 record so that CompactV2 output
    /// is byte-identical for round-tripped plans.
    /// </summary>
    public static ResponsePlan ToV2(PlanV3 v3)
    {
        var acks = new List<Acknowledgment>();
        var content = new List<PlannedContent>();
        var epistemic = new List<EpistemicNote>();
        PlannedQuestion? question = null;

        foreach (var i in v3.Items)
        {
            switch (i.Type)
            {
                case "correction" or "agreement" or "teaching" or "answer-received" or "acknowledgment":
                    acks.Add(new Acknowledgment(
                        i.Value?["kind"]?.GetValue<string>() is { } k && Enum.TryParse<AckKind>(k, out var kind)
                            ? kind : AckKind.FactTaught,
                        i.Value?["owner"]?.GetValue<string>() switch
                        {
                            "companion" => ErrorOwner.Companion,
                            "user" => ErrorOwner.User,
                            _ => ErrorOwner.Nobody,
                        },
                        i.Text ?? ""));
                    break;

                case "knowledge-boundary":
                    var ek = i.Value?["kind"]?.GetValue<string>() switch
                    {
                        "uncertain" => EpistemicKind.Uncertain,
                        "disputed" => EpistemicKind.Disputed,
                        _ => EpistemicKind.NotLearned,
                    };
                    epistemic.Add(new EpistemicNote(ek, i.Text ?? ""));
                    break;

                case "clarify" or "curiosity":
                    question = new PlannedQuestion(
                        i.Type == "clarify" ? QuestionKind.Clarify : QuestionKind.Curiosity,
                        i.Text ?? "",
                        Mandatory: i.Policy == ExpressionPolicy.ask_required);
                    break;

                default:
                    if (i.Policy == ExpressionPolicy.background_only)
                        break; // no safe v2 carrier — dropped, never demoted into PALETTE
                    content.Add(new PlannedContent(
                        i.Type switch
                        {
                            "interpretation" => ContentKind.Interpretation,
                            "shared-memory" => ContentKind.SharedMemory,
                            "knowledge" => ContentKind.LearnedKnowledge,
                            _ => ContentKind.Memory,
                        },
                        i.Policy switch
                        {
                            ExpressionPolicy.must_express => ContentRequirement.MustState,
                            ExpressionPolicy.may_express => ContentRequirement.MayUse,
                            _ => ContentRequirement.MustNotContradict,
                        },
                        i.Text ?? "",
                        i.Provenance?.Origin));
                    break;
            }
        }

        return new ResponsePlan
        {
            TraceId = v3.TraceId,
            Act = KebabToIntent(v3.Act),
            Acknowledgments = acks,
            Content = content,
            Epistemic = epistemic,
            Question = question,
            Tone = SplitLegacyStyle(v3.Register.LegacyStyle),
        };
    }

    private static TurnIntent KebabToIntent(string kebab)
    {
        foreach (var v in Enum.GetValues<TurnIntent>())
            if (v.ToKebab() == kebab)
                return v;
        return TurnIntent.Unknown;
    }

    private static ToneGuidance SplitLegacyStyle(string? legacy)
    {
        var parts = (legacy ?? "").Split("; ", 3);
        return new ToneGuidance(
            parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null,
            parts.Length > 1 ? parts[1] : null,
            parts.Length > 2 ? parts[2] : null);
    }
}
