using Companion.Core.Domain;
using Companion.PlanV3;

namespace Companion.Core.Abstractions;

/// <summary>
/// The executive planner's answer: the plan to render (the refined one when refinement was
/// proposed, applied and validated; otherwise the deterministic plan it started from,
/// unchanged) and the decision record saying which of those happened and why.
/// </summary>
public sealed record ExecutivePlanOutcome(PlanV3.PlanV3 Plan, DecisionRecord Decision);

/// <summary>
/// A model in the PLANNING seat, never the speaking seat.
///
/// It consumes the deterministically built, authority-assembled native plan/4 plus the turn's
/// message and proposes a bounded refinement - which optional items to include, their order,
/// and whether to use an optional question. It cannot add items, change an item's policy,
/// alter must/never/admit obligations, or emit a single word the user will read: its output is
/// a transform on typed plan state, re-validated structurally, for audience, and for render
/// eligibility before anything downstream sees it. Any failure - transport, parse, an
/// out-of-bounds proposal, a validation error - returns the deterministic plan untouched.
/// </summary>
public interface IExecutivePlanner
{
    /// <summary>False when no planner model is configured; callers then skip the call.</summary>
    bool IsEnabled { get; }

    Task<ExecutivePlanOutcome> RefineAsync(
        PlanV3.PlanV3 deterministicPlan,
        string userMessage,
        CancellationToken ct = default);
}

/// <summary>The no-model planner: the deterministic plan is the plan.</summary>
public sealed class NullExecutivePlanner : IExecutivePlanner
{
    public bool IsEnabled => false;

    public Task<ExecutivePlanOutcome> RefineAsync(
        PlanV3.PlanV3 deterministicPlan, string userMessage, CancellationToken ct = default)
        => Task.FromResult(new ExecutivePlanOutcome(deterministicPlan, new DecisionRecord
        {
            Stage = "plan.executive",
            Decider = "rule",
            Verdict = "deterministic",
            Reason = "no executive planner model configured",
        }));
}
