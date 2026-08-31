using Companion.Core.Domain;
using Companion.PlanV3;

namespace Companion.Core.Abstractions;

/// <summary>
/// Typed turn state handed to the executive planner. Everything here already passed through
/// the deterministic understanding pipeline; nothing is raw model output.
/// </summary>
public sealed record PlanningSignals
{
    public required string UserMessage { get; init; }

    /// <summary>Recent turns, oldest first, capped by the caller.</summary>
    public IReadOnlyList<(string Role, string Text)> Recent { get; init; } = [];

    /// <summary>Retrieved memories WITH their identities, so a grounded proposal can cite one.</summary>
    public IReadOnlyList<(Guid Id, string Text)> Memories { get; init; } = [];

    /// <summary>Bounded tool-result text the loop already granted, or null.</summary>
    public string? ToolResults { get; init; }

    /// <summary>The move the previous turn left open, when one is.</summary>
    public PendingMove? Pending { get; init; }

    /// <summary>
    /// How the user's message landed against <see cref="Pending"/> - decided by the existing
    /// typed understanding (answer binding), never by this planner. Null when no move was
    /// pending or the message did not engage it.
    /// </summary>
    public MoveResolution? PendingResolution { get; init; }

    /// <summary>
    /// Whether creative invention is licensed this turn. Typed sources only: an active fiction
    /// frame or a detected in-character turn. Without this, a creative proposal is refused.
    /// </summary>
    public bool CreativeInvited { get; init; }
}

/// <summary>
/// The executive planner's answer: the plan to render (refined and re-validated, or the
/// deterministic plan untouched) and the decision record saying which happened and why.
/// </summary>
public sealed record ExecutivePlanOutcome(PlanV3.PlanV3 Plan, DecisionRecord Decision);

/// <summary>
/// A model in the PLANNING seat, never the speaking seat.
///
/// It consumes the deterministically built, authority-assembled native plan/4 plus typed turn
/// signals, and proposes a bounded refinement:
///
///  - selecting and ordering optional (may_express) items, and declining an optional question;
///  - PROPOSING new plan items - typed content with provenance and epistemic status, never
///    final prose. A grounded proposal must cite the memory or item it stands on; an
///    inference is marked as one; uncertainty becomes an admit_unknown item; creative
///    invention is admissible only when the turn's typed state invited it.
///
/// The AUTHORITY LAYER - deterministic code, not the model - decides what enters the plan:
/// proposals enter as may_express or admit_unknown only, obligations (must_express,
/// must_not_express, privacy, tool authorization) are not reachable from here, a proposal
/// overlapping a suppressed item's content is refused, and the whole refined plan re-passes
/// structural, audience and render-eligibility validation or the deterministic plan stands.
/// The model cannot grant itself authority, and it never writes the displayed response.
/// </summary>
public interface IExecutivePlanner
{
    /// <summary>False when no planner model is configured; callers then skip the call.</summary>
    bool IsEnabled { get; }

    Task<ExecutivePlanOutcome> RefineAsync(
        PlanV3.PlanV3 deterministicPlan,
        PlanningSignals signals,
        CancellationToken ct = default);
}

/// <summary>The no-model planner: the deterministic plan is the plan.</summary>
public sealed class NullExecutivePlanner : IExecutivePlanner
{
    public bool IsEnabled => false;

    public Task<ExecutivePlanOutcome> RefineAsync(
        PlanV3.PlanV3 deterministicPlan, PlanningSignals signals, CancellationToken ct = default)
        => Task.FromResult(new ExecutivePlanOutcome(deterministicPlan, new DecisionRecord
        {
            Stage = "plan.executive",
            Decider = "rule",
            Verdict = "deterministic",
            Reason = "no executive planner model configured",
        }));
}
