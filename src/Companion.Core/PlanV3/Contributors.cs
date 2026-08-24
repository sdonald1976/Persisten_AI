using System.Text.Json.Nodes;

namespace Companion.PlanV3;

/// <summary>
/// The registered capability set (P5). Registration IS the authority model: anything not
/// here reaches background_only at most, and privileged reason families are owned by
/// exactly the subsystems that hold the underlying state.
/// </summary>
public static class SourceRegistry
{
    private static Grant G(RenderCategory category, ExpressionPolicy policy, string useCase,
        string? reasonPrefix = null, string[]? origins = null, bool evidence = false,
        bool promotable = false)
        => new()
        {
            Category = category, Policy = policy, UseCase = useCase,
            ReasonPrefix = reasonPrefix,
            RequiredOrigins = new HashSet<string>(origins ?? []),
            RequiresEvidence = evidence, PromotionAllowed = promotable,
        };

    private static SourceCapability Cap(string id, string origin, Grant[] grants,
        bool questions = false, bool register = false, bool restrictions = false,
        Disclosure disclosure = Disclosure.participants, Retention retention = Retention.full,
        ExpressionPolicy? fallback = ExpressionPolicy.background_only)
        => new()
        {
            SourceId = id, Grants = grants, DefaultOrigin = origin,
            MayProposeQuestions = questions, MayInfluenceRegister = register,
            MayProposeRegisterRestrictions = restrictions,
            DefaultDisclosure = disclosure, DefaultRetention = retention,
            FallbackPolicy = fallback,
        };

    public static IReadOnlyDictionary<string, SourceCapability> Default { get; } =
        new[]
        {
            // PROCEDURE (audited P5b): may ask the activity's own question and frame the
            // activity. NO general must_express. Its epistemic-integrity authority is
            // scoped to `activity-state.` — it can mark its OWN prior activity state
            // stale, never unrelated conversational knowledge — and requires evidence
            // (the activity instance id).
            Cap("procedure", "derived",
            [
                G(RenderCategory.clarify, ExpressionPolicy.ask_required,
                    "the next question selected by the active procedure instance"),
                G(RenderCategory.curiosity, ExpressionPolicy.ask_required,
                    "an activity question phrased as curiosity by the procedure"),
                G(RenderCategory.state, ExpressionPolicy.background_only,
                    "minimal activity framing: activity name, asker role, question number"),
                G(RenderCategory.state, ExpressionPolicy.must_not_express,
                    "retiring the procedure's OWN superseded activity state",
                    reasonPrefix: "epistemic-integrity.activity-state.", evidence: true),
            ], questions: true),

            // TOOL RESULTS: processing context; promotion only via the planner, and only
            // for the claim category (a tool cannot promote an observation into a claim).
            Cap("tool", "tool",
            [
                G(RenderCategory.observation, ExpressionPolicy.background_only,
                    "structured tool output as processing context", origins: ["tool"]),
                G(RenderCategory.claim, ExpressionPolicy.background_only,
                    "a disclosable result awaiting planner authorization", origins: ["tool"]),
                G(RenderCategory.claim, ExpressionPolicy.must_express,
                    "a disclosable result the planner requires in the reply",
                    origins: ["tool"], promotable: true),
                // A failure may be ACKNOWLEDGED, never compelled, and the tuple is scoped
                // by reason prefix so a SUCCESS (which carries no reason code) can never
                // reach may_express through it. Source 2.
                G(RenderCategory.claim, ExpressionPolicy.may_express,
                    "acknowledging that a requested tool call did not succeed",
                    reasonPrefix: "tool-failure.", origins: ["tool"], promotable: true),
            ], retention: Retention.no_training),

            Cap("tool-authorization", "derived",
            [
                G(RenderCategory.note, ExpressionPolicy.must_not_express,
                    "withholding an unauthorized tool result",
                    reasonPrefix: "tool-authorization.", evidence: false),
            ]),

            // PERCEPTION: background by default; promotion to may_express only, never to
            // must_express — physical truth stays AvaWorld's, not the mouth's.
            Cap("world", "observed",
            [
                G(RenderCategory.observation, ExpressionPolicy.background_only,
                    "world observation as tone context", origins: ["observed"]),
                G(RenderCategory.observation, ExpressionPolicy.may_express,
                    "an observation the planner chose to mention", origins: ["observed"], promotable: true),
                G(RenderCategory.state, ExpressionPolicy.background_only,
                    "world state as tone context", origins: ["observed"]),
            ]),
            Cap("vision", "observed",
            [
                G(RenderCategory.observation, ExpressionPolicy.background_only,
                    "visual observation as tone context", origins: ["observed"]),
                G(RenderCategory.observation, ExpressionPolicy.may_express,
                    "a visual observation the planner chose to mention", origins: ["observed"], promotable: true),
            ]),
            Cap("embodiment", "observed",
            [
                G(RenderCategory.observation, ExpressionPolicy.background_only,
                    "embodiment signal as tone context", origins: ["observed"]),
                G(RenderCategory.state, ExpressionPolicy.background_only,
                    "embodiment state as tone context", origins: ["observed"]),
            ]),

            // REGISTER SOURCES: votes only — zero item grants, so none of them can put a
            // single word into the plan.
            Cap("persona", "derived", [], register: true),
            Cap("relationship", "derived", [], register: true),
            Cap("mood", "derived", [], register: true),
            Cap("working-context-register", "derived", [], register: true),
            Cap("mirror", "observed", [], register: true),
            Cap("user-preference", "told-by-user", [], register: true, restrictions: true),
            Cap("hosting-config", "derived", [], register: true, restrictions: true),
        }
        .ToDictionary(c => c.SourceId);
}

/// <summary>
/// Procedure contributor: the ledger stays UPSTREAM and selects the next question; the
/// plan receives the selected question plus minimal rendering context. The mouth never
/// reasons over game state, and repeated questions are prevented upstream by the ledger
/// (which knows what was already asked) — not by the renderer noticing.
/// </summary>
public sealed class ProcedureContributor(ProcedureContributor.ActivityLedger? ledger) : IPlanV3Contributor
{
    /// <summary>
    /// Upstream activity state. Owns question numbering, asked questions, answers,
    /// established facts, exclusions, and candidates; selects the next question.
    /// </summary>
    public sealed record ActivityLedger(
        string ActivityName,
        int QuestionNumber,
        int QuestionBudget,
        IReadOnlyList<string> AskedQuestions,
        IReadOnlyList<(string Question, bool Answer)> Answers,
        IReadOnlyList<string> EstablishedFacts,
        IReadOnlyList<string> Exclusions,
        IReadOnlyList<string> Candidates,
        string? SelectedNextQuestion)
    {
        /// <summary>Upstream repeated-question prevention: a question already asked (or
        /// already settled as an established fact/exclusion) is never selected again.</summary>
        public bool WouldRepeat(string question)
            => AskedQuestions.Any(q => string.Equals(q, question, StringComparison.OrdinalIgnoreCase));

        public string? SelectNext(IEnumerable<string> pool)
            => pool.FirstOrDefault(q => !WouldRepeat(q));
    }

    public string SourceId => "procedure";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        if (ledger is null)
            return PlanContributionResult.Empty;

        var items = new List<ProposedItem>();
        if (ledger.SelectedNextQuestion is { } q)
            items.Add(new ProposedItem
            {
                LocalId = "next-question",
                Type = "activity-question",
                Category = RenderCategory.clarify,
                ProposedPolicy = ExpressionPolicy.ask_required,
                Text = q,
                Provenance = new Provenance(Origin: "derived"),
            });

        // Minimal rendering context ONLY — never the ledger, never the fact list.
        items.Add(new ProposedItem
        {
            LocalId = "activity-frame",
            Type = "activity-state",
            Category = RenderCategory.state,
            ProposedPolicy = ExpressionPolicy.background_only,
            Text = $"{ledger.ActivityName}: Ava asks; question {ledger.QuestionNumber} of {ledger.QuestionBudget}.",
            Provenance = new Provenance(Origin: "derived"),
        });
        return new PlanContributionResult(items);
    }
}

/// <summary>
/// P5a SYNTHETIC tool contributor, retained only as the fixture the grant-authority tests
/// adjudicate against. The PRODUCTION path is
/// <see cref="ToolOutcomeContributor"/>, which reads typed
/// <c>ToolExecutionOutcome</c> captured at execution time; this one takes hand-built
/// booleans and is never registered in the live pipeline.
///
/// The six states are separate facts, and only the last two can make a
/// result expressible: requested / authorized / executed / succeeded-failed / disclosure
/// permitted / required in the reply. A returned string is DATA — never protocol
/// instruction — and secret-bearing results never reach persisted text.
/// </summary>
public sealed class ToolContributor(IReadOnlyList<ToolContributor.ToolOutcome> outcomes) : IPlanV3Contributor
{
    public sealed record ToolOutcome(
        string Tool,
        bool Requested,
        bool Authorized,
        bool Executed,
        bool Succeeded,
        bool DisclosurePermitted,
        bool RequiredInReply,
        string? ResultText,
        bool ContainsSecret = false);

    public string SourceId => "tool";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        var items = new List<ProposedItem>();
        foreach (var o in outcomes)
        {
            // A result nobody authorized, or that carries a secret, contributes NOTHING —
            // not even as background: there is no lawful reading of it this turn.
            if (!o.Requested || !o.Authorized || !o.Executed || o.ContainsSecret)
                continue;

            var expressible = o.Succeeded && o.DisclosurePermitted;
            items.Add(new ProposedItem
            {
                LocalId = $"{o.Tool}-result",
                Type = "tool-result",
                Category = expressible ? RenderCategory.claim : RenderCategory.observation,
                // Even a disclosable result only asks; the assembler grants expression
                // solely when the planner marked it required (PlanningPromotion).
                ProposedPolicy = expressible && o.RequiredInReply
                    ? ExpressionPolicy.must_express
                    : ExpressionPolicy.background_only,
                PlanningPromotion = expressible && o.RequiredInReply,
                Text = o.Succeeded ? o.ResultText : $"The {o.Tool} lookup did not succeed.",
                Quoted = o.Succeeded && o.ResultText is not null,
                Provenance = new Provenance(Origin: "tool"),
                Retention = Retention.no_training,
            });
        }
        return new PlanContributionResult(items);
    }
}

/// <summary>
/// World / vision / embodiment observations: background_only by default, carrying
/// confidence, validity, source, and provenance. Expired or low-confidence observations
/// are dropped rather than becoming factual claims; promotion requires a deliberate
/// planner decision, which the assembler records.
/// </summary>
public sealed class PerceptionContributor(
    string sourceId,
    IReadOnlyList<PerceptionContributor.Observation> observations,
    DateTimeOffset now,
    double minimumConfidence = 0.4) : IPlanV3Contributor
{
    public sealed record Observation(
        string Text, double Confidence, DateTimeOffset? ExpiresAt = null,
        bool PlannerPromoted = false);

    public string SourceId => sourceId;

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        var items = new List<ProposedItem>();
        foreach (var o in observations)
        {
            if (o.ExpiresAt is { } expiry && expiry <= now)
                continue;                              // expired: never a claim
            if (o.Confidence < minimumConfidence && o.PlannerPromoted)
                continue;                              // too thin to promote to speech
            items.Add(new ProposedItem
            {
                LocalId = $"obs-{items.Count + 1}",
                Type = "observation",
                Category = RenderCategory.observation,
                ProposedPolicy = o.PlannerPromoted
                    ? ExpressionPolicy.may_express
                    : ExpressionPolicy.background_only,
                PlanningPromotion = o.PlannerPromoted,
                Text = o.Text,
                Confidence = o.Confidence,
                Validity = o.ExpiresAt is null ? null : new Validity(Until: o.ExpiresAt),
                Provenance = new Provenance(Origin: "observed", At: now),
            });
        }
        return new PlanContributionResult(items);
    }
}

/// <summary>Typed register contributor — one per owning subsystem, votes only.</summary>
public sealed class RegisterContributor(
    string sourceId, IReadOnlyList<RegisterProposal> votes) : IPlanV3Contributor
{
    public string SourceId => sourceId;

    public PlanContributionResult Contribute(PlanContributionContext context)
        => new([], votes);
}
