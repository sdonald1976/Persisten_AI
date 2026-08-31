using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Builds the turn's <see cref="ResponsePlan"/> from state the pipeline has already
/// decided — no model calls, no new cognition, a consolidation with authority levels.
/// SHADOW in stage 1: the plan is computed and recorded beside every turn while the
/// generation packet stays byte-identical, so plan fidelity is measured before the plan
/// is ever rendered (docs/RESPONSE_PLAN.md §9).
/// </summary>
public static class ResponsePlanner
{
    private const int MaxContent = 8;
    private const int MaxTextChars = 200;

    public static ResponsePlan Build(
        Guid traceId,
        TurnIntentState intent,
        WorkingContextState working,
        string userMessage,
        IReadOnlyList<RetrievalResult> retrieved,
        ConceptLookupResult? knowledge,
        string? curiosityQuestion,
        string? registerNote,
        string? moodNote,
        string? personaStyle)
    {
        var acks = new List<Acknowledgment>();
        var content = new List<PlannedContent>();
        var epistemic = new List<EpistemicNote>();

        // ---- acknowledgments: what this turn OWES ----
        if (working.Move == ConversationMove.Correction)
        {
            acks.Add(new Acknowledgment(
                AckKind.CorrectionAccepted,
                working.CorrectionTarget ?? ErrorOwner.Nobody,
                Clip(userMessage)));
        }
        if (working.Move == ConversationMove.ConfirmsClaim)
        {
            // Emphatic agreement with her claim: no error exists, contrition would be
            // invented (the Mad Hatter inversion, canonical regression).
            acks.Add(new Acknowledgment(AckKind.AgreementConfirmed, ErrorOwner.Nobody, Clip(userMessage)));
        }
        if (working is { Move: ConversationMove.AnswersOpenQuestion, BoundQuestion: { } bound })
        {
            acks.Add(new Acknowledgment(AckKind.AnswerReceived, ErrorOwner.Nobody,
                $"their \"{Clip(userMessage)}\" answered: \"{Clip(bound)}\""));
        }
        // Teaching is detected (not performed) here — the pipeline learns it after the
        // reply; the plan only needs to know the turn taught something worth acknowledging.
        if (TeachingDetector.Detect(userMessage) is { } teaching)
            acks.Add(new Acknowledgment(AckKind.FactTaught, ErrorOwner.Nobody, Clip(teaching.Sentence)));

        // ---- content: authority levels over what the packet already selected ----
        if (working.InterpretationNote is { } interpretation)
            content.Add(new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                Clip(interpretation), "working-context"));

        foreach (var result in retrieved.Take(MaxContent))
        {
            var memory = result.Memory;
            var requirement = memory.Status is MemoryStatus.Disputed or MemoryStatus.Superseded
                ? ContentRequirement.MustNotContradict
                : ContentRequirement.MayUse;
            var kind = memory switch
            {
                ConceptAssertion => ContentKind.LearnedKnowledge,
                _ when memory.Owner == MemoryOwner.Shared => ContentKind.SharedMemory,
                _ => ContentKind.Memory,
            };
            content.Add(new PlannedContent(kind, requirement, Clip(memory.Content),
                memory.Owner == MemoryOwner.Shared ? "shared-history" : memory.Status.ToString().ToLowerInvariant())
                { SourceMemoryId = memory.Id });
        }

        // ---- epistemic constraints (Phase 3's boundary, carried as typed state) ----
        if (knowledge is not null)
        {
            switch (knowledge.Familiarity)
            {
                case ConceptFamiliarity.Unknown:
                    epistemic.Add(new EpistemicNote(EpistemicKind.NotLearned, knowledge.Term));
                    break;
                case ConceptFamiliarity.Heard or ConceptFamiliarity.Learning:
                    epistemic.Add(new EpistemicNote(EpistemicKind.Uncertain, knowledge.Term));
                    break;
                case ConceptFamiliarity.Disputed:
                    epistemic.Add(new EpistemicNote(EpistemicKind.Disputed, knowledge.Term));
                    break;
                case ConceptFamiliarity.Known when knowledge.Definition is { } definition:
                    content.Add(new PlannedContent(ContentKind.LearnedKnowledge,
                        ContentRequirement.MustState, Clip(definition), "taught"));
                    break;
            }
        }

        // ---- the question: clarify is the act's own demand; a curiosity is an offer ----
        PlannedQuestion? question = null;
        if (intent.Intent == TurnIntent.Clarify)
        {
            question = new PlannedQuestion(QuestionKind.Clarify,
                $"which \"{working.ReferenceMarkers.FirstOrDefault() ?? "reference"}\" they mean",
                Mandatory: true);
        }
        else if (curiosityQuestion is not null)
        {
            question = new PlannedQuestion(QuestionKind.Curiosity, Clip(curiosityQuestion), Mandatory: false);
        }

        return new ResponsePlan
        {
            TraceId = traceId,
            Act = intent.Intent,
            Acknowledgments = acks,
            Content = content,
            Epistemic = epistemic,
            Question = question,
            Tone = new ToneGuidance(registerNote, moodNote, ClipOrNull(personaStyle)),
        };
    }

    /// <summary>Word-safe clip: never cuts mid-word ("defend the ear"), backs up to the
    /// last space when one exists in the tail and marks the elision.</summary>
    private static string Clip(string text)
    {
        if (text.Length <= MaxTextChars)
            return text;
        var cut = text[..MaxTextChars];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > MaxTextChars / 2 ? cut[..lastSpace] : cut) + "…";
    }

    private static string? ClipOrNull(string? text) =>
        text is null ? null : Clip(text);
}

/// <summary>
/// Deterministic fidelity checks of a REPLY against the plan — the measurable half of the
/// renderer contract, runnable in shadow today. Each check names the invariant it guards
/// (docs/RESPONSE_PLAN.md §5) and returns null when faithful, else the violation.
/// </summary>
public static partial class PlanFidelity
{
    /// <summary>Invariant 3: who made the error. After a Companion-owned correction,
    /// error-sharing language ("we both slipped up") redistributes an error the system
    /// knows the owner of. The Mad Hatter check.</summary>
    public static string? CheckCorrectionOwnership(ResponsePlan plan, string reply)
    {
        if (!plan.Acknowledgments.Any(a => a is { Kind: AckKind.CorrectionAccepted, ErrorOwner: ErrorOwner.Companion }))
            return null;
        var shared = ErrorSharing().Match(reply);
        return shared.Success ? $"error-sharing language after a companion-owned correction: \"{shared.Value}\"" : null;
    }

    /// <summary>Invariant 3's mirror: after emphatic AGREEMENT, apology language claims an
    /// error that never happened — invented contrition, the Mad Hatter inversion live
    /// specimen ("I owe a apology for that mix-up!").</summary>
    public static string? CheckInventedContrition(ResponsePlan plan, string reply)
    {
        if (!plan.Acknowledgments.Any(a => a.Kind == AckKind.AgreementConfirmed))
            return null;
        var contrition = Contrition().Match(reply);
        return contrition.Success
            ? $"apology for a nonexistent error after agreement: \"{contrition.Value}\"" : null;
    }

    /// <summary>Invariant 8: shared history exists only in Shared-owner memory. The reply
    /// may claim "remember when we…" only when the plan carries SharedMemory content.
    /// The rabbit-hole check.</summary>
    public static string? CheckSharedHistoryClaim(ResponsePlan plan, string reply)
    {
        var claim = SharedHistoryClaim().Match(reply);
        if (!claim.Success)
            return null;
        return plan.Content.Any(c => c.Kind == ContentKind.SharedMemory)
            ? null
            : $"unsupported shared-history claim: \"{claim.Value}\"";
    }

    /// <summary>Invariants 1–2: epistemic state. A NotLearned subject explained at length
    /// is pretrained knowledge presented as hers. Coarse on purpose — the deterministic
    /// tripwire, not a semantic judge.</summary>
    public static string? CheckEpistemic(ResponsePlan plan, string reply)
    {
        foreach (var note in plan.Epistemic.Where(n => n.Kind == EpistemicKind.NotLearned))
        {
            var subject = note.Subject.Trim();
            // The EXPLANATION shape: copula + category ("a quokka is a small wallaby…").
            // "I haven't learned what a quokka is yet" shares the words and must not trip.
            if (Regex.IsMatch(reply,
                    $@"\b{Regex.Escape(subject)}s?\b\s+((is|are)\s+(a|an|the)\s+\w|means\s+\w)",
                    RegexOptions.IgnoreCase))
                return $"explained \"{subject}\" while the plan says not-learned";
        }
        return null;
    }

    [GeneratedRegex(@"\b(we both|both of us|we all|we've both|we each)\b[^.!?]*\b(wrong|mistake|mixed|slipped|confused|err)\w*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ErrorSharing();

    [GeneratedRegex(@"\b(sorry|apolog\w+|my (mistake|bad|error)|i (was wrong|mixed (it|that) up|misspoke|owe .{0,12}apology)|forgive me)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Contrition();

    [GeneratedRegex(@"\b(remember (when|that time) we|that time we (went|did|had|tried)|like when we (went|did|had|tried)|we used to (go|do|have))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SharedHistoryClaim();
}
