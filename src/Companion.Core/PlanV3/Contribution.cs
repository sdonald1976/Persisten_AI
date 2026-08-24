using System.Text.Json.Nodes;

namespace Companion.PlanV3;

/// <summary>
/// The generic contribution boundary (P5, docs/RESPONSE_PLAN_V3_SPEC.md §15). A
/// contributor PROPOSES typed facts and state; it never produces serialized prose, never
/// a whole plan, and never its own authority. The assembler
/// (<see cref="PlanV3Assembler"/>) validates provenance, approves or downgrades every
/// proposed policy against the contributor's registered capability, resolves conflicts,
/// applies disclosure/retention, enforces recipient authorization, applies budgets, and
/// alone produces the final native plan.
///
/// This exists so `PlanV3Builder` never becomes a switch statement containing every
/// future organ: a new organ ships a contributor plus a capability declaration, and the
/// assembler's rules are what keep it honest.
/// </summary>
public interface IPlanV3Contributor
{
    /// <summary>Kebab id; must match a registered <see cref="SourceCapability"/>.</summary>
    string SourceId { get; }

    PlanContributionResult Contribute(PlanContributionContext context);
}

/// <summary>Read-only turn context handed to contributors. Deliberately narrow.</summary>
public sealed record PlanContributionContext(
    Guid TraceId,
    string Act,
    string UserMessage,
    string UserParticipantId,
    string CompanionParticipantId,
    bool SensitiveTurn,
    IReadOnlyDictionary<string, object>? Extras = null);

/// <summary>What a contributor proposes. Items are PROPOSALS; register entries are votes.</summary>
public sealed record PlanContributionResult(
    IReadOnlyList<ProposedItem> Items,
    IReadOnlyList<RegisterProposal>? Register = null,
    string? Error = null)
{
    public static PlanContributionResult Empty { get; } = new([]);
    public static PlanContributionResult Failed(string error) => new([], null, error);
}

/// <summary>
/// A proposed item. `ProposedPolicy` is a REQUEST — the assembler grants it only if the
/// contributor's capability allows that policy, and otherwise downgrades to the
/// capability's fallback (or rejects) with a diagnosed reason.
/// </summary>
public sealed record ProposedItem
{
    public required string LocalId { get; init; }
    public required string Type { get; init; }
    public required RenderCategory Category { get; init; }
    public required ExpressionPolicy ProposedPolicy { get; init; }
    public string? Text { get; init; }
    public JsonNode? Value { get; init; }
    public bool Quoted { get; init; }
    public Provenance? Provenance { get; init; }
    public double? Confidence { get; init; }
    public Validity? Validity { get; init; }
    public string? ReasonCode { get; init; }
    public Classification? Classification { get; init; }
    public Disclosure? Disclosure { get; init; }
    public Retention? Retention { get; init; }
    public string? Owner { get; init; }
    public IReadOnlyList<string>? Audience { get; init; }
    public int? Priority { get; init; }

    /// <summary>
    /// Set by cognition (not by the contributing organ) to request that a normally
    /// background-only observation or tool result be EXPRESSED this turn. The assembler
    /// still checks the capability's promotion permission and records the promotion.
    /// </summary>
    public bool PlanningPromotion { get; init; }
}

/// <summary>A register vote for one dimension, owned and reasoned.</summary>
public sealed record RegisterProposal(
    string Dimension,
    string Value,
    string ReasonCode,
    Provenance? Provenance = null,
    bool Restrictive = false);

/// <summary>
/// What a source is ALLOWED to do. Registration is the entire authority model: an
/// unregistered source cannot reach must_express, must_not_express, ask_required, register
/// restrictions, or any privileged reason-code family — its informational items become
/// background_only pending registration, diagnosed, never silently promoted.
/// </summary>
public sealed record SourceCapability
{
    public required string SourceId { get; init; }
    public required IReadOnlySet<RenderCategory> AllowedCategories { get; init; }
    public required IReadOnlySet<ExpressionPolicy> AllowedPolicies { get; init; }
    public IReadOnlySet<string> ReasonCodeFamilies { get; init; } = new HashSet<string>();
    public bool MayProposeQuestions { get; init; }
    public bool MayInfluenceRegister { get; init; }
    public bool MayProposeRegisterRestrictions { get; init; }

    /// <summary>Provenance origins this source may claim; empty = any well-known origin.</summary>
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>();

    /// <summary>Origin stamped when the contributor supplies none.</summary>
    public required string DefaultOrigin { get; init; }

    public Disclosure DefaultDisclosure { get; init; } = Disclosure.participants;
    public Retention DefaultRetention { get; init; } = Retention.full;

    /// <summary>Policy granted when a proposal exceeds capability but is still usable.
    /// Null means such proposals are REJECTED rather than downgraded.</summary>
    public ExpressionPolicy? FallbackPolicy { get; init; } = ExpressionPolicy.background_only;

    /// <summary>May cognition promote this source's background items to expression?</summary>
    public bool PromotableByPlanner { get; init; }
}

/// <summary>Per-contribution outcome for diagnostics; content-safe by construction.</summary>
public sealed record ContributionOutcome(
    string SourceId, string LocalId, string Decision, string? Reason,
    ExpressionPolicy? Proposed, ExpressionPolicy? Granted);

/// <summary>Register conflict resolution record: who won a dimension, and why.</summary>
public sealed record RegisterDecision(
    string Dimension, string Value, string WinningSource, string ReasonCode,
    IReadOnlyList<string> Losers);

/// <summary>The assembler's full, content-safe report for one turn.</summary>
public sealed record AssemblyReport
{
    public required PlanV3 Plan { get; init; }
    public IReadOnlyList<ContributionOutcome> Outcomes { get; init; } = [];
    public IReadOnlyList<RegisterDecision> RegisterDecisions { get; init; } = [];
    public IReadOnlyList<string> AuthorityViolations { get; init; } = [];
    public IReadOnlyList<string> ContributorFailures { get; init; } = [];
    public IReadOnlyList<string> LintRejections { get; init; } = [];
    public IReadOnlyList<string> Provenance { get; init; } = [];

    public int Received => Outcomes.Count;
    public int Accepted => Outcomes.Count(o => o.Decision is "accepted" or "downgraded" or "promoted");
    public int Rejected => Outcomes.Count(o => o.Decision == "rejected");
    public int Promotions => Outcomes.Count(o => o.Decision == "promoted");
}
