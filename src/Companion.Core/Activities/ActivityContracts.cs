namespace Companion.Core.Activities;

/// <summary>
/// The strategy boundary (Source 1b, docs/SOURCE1B_SELECTOR_DESIGN.md §A1). The RUNTIME
/// owns persistence, lifecycle, identities, transactions, retries, idempotency, isolation,
/// and every authority decision, and knows nothing about any particular activity. The
/// STRATEGY owns activity-specific state transitions and move selection — and can never
/// write the ledger: it returns proposals and results the runtime applies.
/// </summary>
public interface IActivityStrategy
{
    string ActivityType { get; }
    string Version { get; }

    StrategyState Initialize(ActivityDefinition definition);

    TransitionResult ApplyInput(ActivityInstance instance, ActivityInput input);

    SelectionOutcome SelectNext(ActivityInstance instance, StrategyState state);

    ValidationResult ValidateSelection(
        ActivityInstance instance, StrategyState state, ActivityMove proposedMove);

    CompletionResult EvaluateCompletion(ActivityInstance instance, StrategyState state);
}

public sealed record ActivityDefinition(
    string ActivityType,
    string Version,
    Guid? ProcedureId,                    // resolves a real Procedure row when taught
    int QuestionLimit,
    string AskerParticipantId,
    string AnswererParticipantId);

/// <summary>Strategy-owned state: opaque to the runtime, persisted verbatim beside the ledger.</summary>
public sealed record StrategyState(
    IReadOnlyList<Hypothesis> Hypotheses,
    IReadOnlyList<EvidenceEntry> Evidence,
    IReadOnlyDictionary<string, string>? Extras = null)
{
    public static StrategyState Empty { get; } = new([], []);

    public IEnumerable<Hypothesis> Live => Hypotheses.Where(h => !h.Excluded);
}

/// <summary>An open-domain candidate. Free-text label — never drawn from a fixed catalog.</summary>
public sealed record Hypothesis(
    string Label, double Confidence, bool Excluded = false, string? ExcludedByQuestionKey = null);

/// <summary>What one answer established: which hypotheses it supported and which it killed.</summary>
public sealed record EvidenceEntry(
    string QuestionKey, bool Answer,
    IReadOnlyList<string> Supports, IReadOnlyList<string> Excludes);

public enum ActivityInputKind { Answer, Correction, Abandon, GuessVerdict, Malformed }

/// <summary>
/// One turn's input, bound to STABLE identities — never "the most recent text".
/// <paramref name="MessageId"/> makes retries idempotent.
/// </summary>
public sealed record ActivityInput(
    ActivityInputKind Kind,
    string? BoundQuestionKey,
    bool? BooleanAnswer,
    string? RawText,
    Guid MessageId,
    DateTimeOffset At);

public sealed record TransitionResult(
    ActivityInstance Instance, StrategyState State, bool Applied, string? RejectionReason);

public enum ActivityMoveKind { Question, Guess }

public enum MoveOrigin { Deterministic, ModelProposal }

public sealed record ActivityMove(
    ActivityMoveKind Kind,
    string StableKey,
    string Text,
    string? Rationale = null,
    IReadOnlyList<string>? Hypotheses = null,
    double? Confidence = null,
    MoveOrigin Origin = MoveOrigin.Deterministic);

public sealed record SelectionOutcome(
    ActivityMove? Move,
    string? FailureReason,
    IReadOnlyList<string> RejectedForRepeat)
{
    public static SelectionOutcome Failed(string reason, IReadOnlyList<string>? repeats = null)
        => new(null, reason, repeats ?? []);
}

public sealed record ValidationResult(bool Valid, string? Reason)
{
    public static ValidationResult Ok { get; } = new(true, null);
    public static ValidationResult Reject(string reason) => new(false, reason);
}

public sealed record CompletionResult(bool Complete, ActivityLifecycle? Lifecycle, string? Reason)
{
    public static CompletionResult Continue { get; } = new(false, null, null);
}

public enum ActivityLifecycle { Proposed, Active, Completed, Abandoned }

/// <summary>
/// The authoritative ledger. Deterministic code alone writes it (§A2): numbering, stable
/// identities, answer binding, repeat rejection, roles, limit, lifecycle, malformed input,
/// corrections, completion, abandonment, and legality of any proposed move.
/// </summary>
public sealed record ActivityInstance
{
    public required string InstanceId { get; init; }
    public required string ActivityType { get; init; }
    public required string StrategyVersion { get; init; }
    public required ActivityLifecycle Lifecycle { get; init; }

    public Guid? ProcedureId { get; init; }
    public required string UserId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string AskerParticipantId { get; init; }
    public required string AnswererParticipantId { get; init; }

    public required int QuestionLimit { get; init; }
    public required int CurrentQuestionNumber { get; init; }

    public IReadOnlyList<AskedQuestion> AskedQuestions { get; init; } = [];
    public IReadOnlyList<AnswerBinding> Answers { get; init; } = [];

    public ActivityMove? PendingMove { get; init; }
    public string? FinalGuess { get; init; }
    public bool? FinalGuessCorrect { get; init; }

    public required DateTimeOffset ActivatedAt { get; init; }
    public string? ActivationEvidence { get; init; }

    /// <summary>Message ids already applied — retries are idempotent (§4 of the brief).</summary>
    public IReadOnlyList<Guid> AppliedMessageIds { get; init; } = [];

    public bool IsActive => Lifecycle == ActivityLifecycle.Active;

    public bool WouldRepeat(string questionKey)
        => AskedQuestions.Any(q => string.Equals(q.Key, questionKey, StringComparison.OrdinalIgnoreCase));

    public bool AlreadyApplied(Guid messageId) => AppliedMessageIds.Contains(messageId);
}

/// <summary>A question with a STABLE key: rephrasing never creates a new question.</summary>
public sealed record AskedQuestion(string Key, string Text, int Number);

public sealed record AnswerBinding(string QuestionKey, bool Answer, Guid MessageId);
