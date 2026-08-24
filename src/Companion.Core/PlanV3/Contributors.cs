using System.Text.Json.Nodes;

namespace Companion.PlanV3;

/// <summary>
/// The registered capability set (P5). Registration IS the authority model: anything not
/// here reaches background_only at most, and privileged reason families are owned by
/// exactly the subsystems that hold the underlying state.
/// </summary>
public static class SourceRegistry
{
    private static SourceCapability Cap(
        string id, RenderCategory[] categories, ExpressionPolicy[] policies, string origin,
        string[]? families = null, bool questions = false, bool register = false,
        bool restrictions = false, Disclosure disclosure = Disclosure.participants,
        Retention retention = Retention.full, ExpressionPolicy? fallback = ExpressionPolicy.background_only,
        bool promotable = false, string[]? origins = null)
        => new()
        {
            SourceId = id,
            AllowedCategories = new HashSet<RenderCategory>(categories),
            AllowedPolicies = new HashSet<ExpressionPolicy>(policies),
            ReasonCodeFamilies = new HashSet<string>(families ?? []),
            MayProposeQuestions = questions,
            MayInfluenceRegister = register,
            MayProposeRegisterRestrictions = restrictions,
            AllowedOrigins = new HashSet<string>(origins ?? []),
            DefaultOrigin = origin,
            DefaultDisclosure = disclosure,
            DefaultRetention = retention,
            FallbackPolicy = fallback,
            PromotableByPlanner = promotable,
        };

    public static IReadOnlyDictionary<string, SourceCapability> Default { get; } =
        new[]
        {
            // Procedures own activity state and next-action selection.
            Cap("procedure",
                [RenderCategory.clarify, RenderCategory.state, RenderCategory.claim],
                [ExpressionPolicy.ask_required, ExpressionPolicy.background_only, ExpressionPolicy.must_express],
                origin: "derived", families: ["epistemic-integrity."], questions: true),

            // Tool RESULTS are processing context by default and promotable only by the
            // planner; the separate tool-authorization source owns disclosure decisions.
            Cap("tool",
                [RenderCategory.observation, RenderCategory.claim, RenderCategory.note],
                [ExpressionPolicy.background_only],
                origin: "tool", retention: Retention.no_training, promotable: true),
            Cap("tool-authorization",
                [RenderCategory.note],
                [ExpressionPolicy.must_not_express, ExpressionPolicy.background_only],
                origin: "derived", families: ["tool-authorization."]),

            // World/perception: physical truth stays AvaWorld's; the mouth gets background.
            Cap("world", [RenderCategory.observation, RenderCategory.state],
                [ExpressionPolicy.background_only], origin: "observed", promotable: true),
            Cap("vision", [RenderCategory.observation],
                [ExpressionPolicy.background_only], origin: "observed", promotable: true),
            Cap("embodiment", [RenderCategory.observation, RenderCategory.state],
                [ExpressionPolicy.background_only], origin: "observed", promotable: true),

            // Register sources: influence only, no items.
            Cap("persona", [], [], origin: "derived", register: true),
            Cap("relationship", [], [], origin: "derived", register: true),
            Cap("mood", [], [], origin: "derived", register: true),
            Cap("working-context-register", [], [], origin: "derived", register: true),
            Cap("mirror", [], [], origin: "observed", register: true),
            Cap("user-preference", [], [], origin: "told-by-user",
                families: ["user-preference."], register: true, restrictions: true),
            Cap("hosting-config", [], [], origin: "derived",
                families: ["hosting-config."], register: true, restrictions: true),
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
/// Tool contributor. The six states are separate facts, and only the last two can make a
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
