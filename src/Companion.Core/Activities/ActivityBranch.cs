namespace Companion.Core.Activities;

/// <summary>
/// Why a branch exists and what it may legally do (Source 1b, counterfactual resolution).
///
/// Natural shadow execution has two realities that must never be confused. The user saw a
/// question the PRODUCTION path produced; the native strategy meanwhile selected a
/// question that was never displayed. Binding the user's real answer to the undisplayed
/// native question would fabricate evidence — the answer was to a different question.
/// </summary>
public enum BranchKind
{
    /// <summary>Production's displayed moves and the real answers to them. Diagnostic
    /// evidence only — never parsed into native training targets.</summary>
    ProductionObserved,

    /// <summary>What the native strategy WOULD have selected at a branch point. Records
    /// the proposal and validation; may never consume subsequent user answers.</summary>
    CounterfactualNative,

    /// <summary>A simulated session where the native move WAS displayed to a simulated
    /// user, so its answers are legally bindable. Labeled simulated, never natural.</summary>
    Simulated,
}

/// <summary>What happened to one move — the field that keeps the branches honest.</summary>
public enum MoveDisposition
{
    /// <summary>Displayed to the real user by the production path.</summary>
    ObservedDisplayed,

    /// <summary>Selected natively and never shown to anyone.</summary>
    CounterfactualNotDisplayed,

    /// <summary>Displayed to a simulated user inside a simulation.</summary>
    SimulatedDisplayed,
}

/// <summary>
/// One recorded move with its disposition and bindability. The rule this type exists to
/// enforce: <see cref="NextInputBindable"/> is true only when the move was actually
/// displayed to whoever is about to answer.
/// </summary>
public sealed record BranchMove
{
    public required string BranchId { get; init; }
    public required string MoveId { get; init; }
    public required ActivityMove Move { get; init; }
    public required MoveDisposition Disposition { get; init; }

    /// <summary>The renderer that produced the displayed text, when one did.</summary>
    public string? DisplayedRenderer { get; init; }

    /// <summary>Identity of the question actually shown, when known.</summary>
    public string? DisplayedQuestionId { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Whether the NEXT user input may be bound to this move. False for every
    /// counterfactual: the user answered something else.
    /// </summary>
    public bool NextInputBindable => Disposition is
        MoveDisposition.ObservedDisplayed or MoveDisposition.SimulatedDisplayed;
}

/// <summary>
/// A branch of activity state. Natural counterfactual branches are single-step by
/// construction: they record a branch point and one hypothetical move, and cannot advance.
/// </summary>
public sealed record ActivityBranch
{
    public required string BranchId { get; init; }
    public required BranchKind Kind { get; init; }

    /// <summary>The branch this one forked from, and the move number it forked at.</summary>
    public string? ParentBranchId { get; init; }
    public int? BranchPointQuestionNumber { get; init; }

    public required ActivityInstance Instance { get; init; }
    public StrategyState State { get; init; } = StrategyState.Empty;
    public IReadOnlyList<BranchMove> Moves { get; init; } = [];

    /// <summary>True only for branches whose moves reach the answerer.</summary>
    public bool CanAdvanceFromUserInput => Kind is BranchKind.ProductionObserved or BranchKind.Simulated;

    /// <summary>A counterfactual is never a completed natural session, whatever its lifecycle.</summary>
    public bool IsReportableNaturalSession
        => Kind == BranchKind.ProductionObserved && Instance.Lifecycle == ActivityLifecycle.Completed;

    public string Label => Kind switch
    {
        BranchKind.Simulated => "simulated",
        BranchKind.CounterfactualNative => "natural-counterfactual",
        _ => "natural-observed",
    };
}

/// <summary>
/// Guards the binding rule at the one place it matters. Any attempt to advance a
/// counterfactual branch with a real user answer is refused with a diagnosed reason
/// rather than silently producing fabricated evidence.
/// </summary>
public static class BranchBinding
{
    public sealed record BindDecision(bool Allowed, string? Reason, BranchMove? Target);

    /// <summary>
    /// Decides whether <paramref name="input"/> may bind to the branch's latest move.
    /// Refuses when the branch cannot advance at all, when there is no move to answer,
    /// when the latest move was never displayed, or when the input names a different
    /// question than the one displayed.
    /// </summary>
    public static BindDecision CanBind(ActivityBranch branch, ActivityInput input)
    {
        if (!branch.CanAdvanceFromUserInput)
            return new BindDecision(false, "counterfactual-branch-cannot-consume-user-input", null);

        var latest = branch.Moves.LastOrDefault();
        if (latest is null)
            return new BindDecision(false, "no-move-awaiting-an-answer", null);

        if (!latest.NextInputBindable)
            return new BindDecision(false, "latest-move-was-not-displayed", latest);

        if (input.BoundQuestionKey is { } key
            && !string.Equals(key, latest.Move.StableKey, StringComparison.OrdinalIgnoreCase))
            return new BindDecision(false, "input-names-a-different-question", latest);

        return new BindDecision(true, null, latest);
    }
}
