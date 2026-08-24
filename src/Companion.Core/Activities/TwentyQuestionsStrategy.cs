using System.Text.RegularExpressions;

namespace Companion.Core.Activities;

/// <summary>
/// Twenty Questions (Source 1b, §A3–A4). Owns hypothesis state, transitions, the
/// deterministic baseline bank, and validation of untrusted model proposals. Writes
/// nothing: every method returns a value the runtime applies.
/// </summary>
public sealed class TwentyQuestionsStrategy : IActivityStrategy
{
    public string ActivityType => "twenty-questions";
    public string Version => "1";

    /// <summary>
    /// The deterministic baseline: a coarse-to-fine typed bank with stable keys. Its
    /// documented limit, stated rather than hidden — it narrows the physical/functional
    /// region but CANNOT identify an arbitrary open-domain object and never guesses. It
    /// exists for reproducible tests, availability when the proposer fails, a benchmark
    /// baseline, and guaranteed-legal questions.
    /// </summary>
    internal static readonly (string Key, string Text)[] Bank =
    [
        ("physical", "does it exist physically"),
        ("man-made", "is it man-made"),
        ("indoors", "is it usually found indoors"),
        ("hand-held", "is it small enough to hold in one hand"),
        ("moving-parts", "does it have moving parts"),
        ("powered", "does it need power to work"),
        ("material-primary", "is it primarily made of metal"),
        ("material-soft", "is it soft to the touch"),
        ("practical", "does it serve a practical purpose"),
        ("kitchen", "does it belong in a kitchen"),
        ("personal", "is it something a person keeps to themselves"),
        ("daily-use", "is it used most days"),
        ("visible-guest", "would a guest see it out in the open"),
        ("purchased", "was it bought rather than made"),
        ("older-than-decade", "is the design older than a decade"),
    ];

    private static readonly Regex Yes = new(@"^\s*(y|yes|yep|yeah|correct|true|right)\b", RegexOptions.IgnoreCase);
    private static readonly Regex No = new(@"^\s*(n|no|nope|nah|incorrect|false|wrong)\b", RegexOptions.IgnoreCase);

    public StrategyState Initialize(ActivityDefinition definition) => StrategyState.Empty;

    /// <summary>
    /// Deterministic transitions. Answers bind to the STABLE key the input carries — never
    /// to the most recent text. Malformed input is rejected without mutating anything; a
    /// correction rebinds the named question and re-derives evidence.
    /// </summary>
    public TransitionResult ApplyInput(ActivityInstance instance, ActivityInput input)
    {
        if (instance.AlreadyApplied(input.MessageId))
            return new TransitionResult(instance, CurrentState(instance), false, "already-applied");

        switch (input.Kind)
        {
            case ActivityInputKind.Abandon:
                return new TransitionResult(
                    instance with
                    {
                        Lifecycle = ActivityLifecycle.Abandoned,
                        AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId],
                    },
                    CurrentState(instance), true, null);

            case ActivityInputKind.Malformed:
                // Recorded as applied so a retry of the same message is idempotent, but the
                // ledger's question state is untouched: no binding, no increment.
                return new TransitionResult(
                    instance with { AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId] },
                    CurrentState(instance), false, "malformed-answer");

            case ActivityInputKind.GuessVerdict:
            {
                if (instance.FinalGuess is null)
                    return new TransitionResult(instance, CurrentState(instance), false, "no-outstanding-guess");
                var correct = input.BooleanAnswer ?? false;
                return new TransitionResult(
                    instance with
                    {
                        FinalGuessCorrect = correct,
                        Lifecycle = correct ? ActivityLifecycle.Completed : instance.Lifecycle,
                        FinalGuess = correct ? instance.FinalGuess : null,
                        PendingMove = null,
                        AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId],
                    },
                    CurrentState(instance), true, null);
            }

            case ActivityInputKind.Correction:
            {
                if (input.BoundQuestionKey is not { } ck || !instance.WouldRepeat(ck))
                    return new TransitionResult(instance, CurrentState(instance), false, "unknown-question-key");
                if (input.BooleanAnswer is not { } cv)
                    return new TransitionResult(instance, CurrentState(instance), false, "malformed-answer");

                var corrected = instance.Answers
                    .Select(a => a.QuestionKey == ck ? a with { Answer = cv, MessageId = input.MessageId } : a)
                    .ToList();
                var fixedInstance = instance with
                {
                    Answers = corrected,
                    AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId],
                };
                return new TransitionResult(fixedInstance, CurrentState(fixedInstance), true, null);
            }

            case ActivityInputKind.Answer:
            {
                if (input.BoundQuestionKey is not { } key)
                    return new TransitionResult(instance, CurrentState(instance), false, "unbound-answer");
                if (!instance.WouldRepeat(key))
                    return new TransitionResult(instance, CurrentState(instance), false, "unknown-question-key");
                if (instance.Answers.Any(a => a.QuestionKey == key))
                    return new TransitionResult(instance, CurrentState(instance), false, "question-already-answered");

                var value = input.BooleanAnswer ?? Parse(input.RawText);
                if (value is not { } answer)
                    return new TransitionResult(
                        instance with { AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId] },
                        CurrentState(instance), false, "malformed-answer");

                var advanced = instance with
                {
                    Answers = [.. instance.Answers, new AnswerBinding(key, answer, input.MessageId)],
                    CurrentQuestionNumber = instance.CurrentQuestionNumber + 1,
                    PendingMove = null,
                    AppliedMessageIds = [.. instance.AppliedMessageIds, input.MessageId],
                };
                return new TransitionResult(advanced, CurrentState(advanced), true, null);
            }

            default:
                return new TransitionResult(instance, CurrentState(instance), false, "unsupported-input");
        }
    }

    /// <summary>Deterministic fallback selection: first legal bank question, coarse to fine.</summary>
    public SelectionOutcome SelectNext(ActivityInstance instance, StrategyState state)
    {
        if (!instance.IsActive)
            return SelectionOutcome.Failed("activity-not-active");
        if (instance.CurrentQuestionNumber > instance.QuestionLimit)
            return SelectionOutcome.Failed("question-limit-reached");

        var repeats = new List<string>();
        foreach (var (key, text) in Bank)
        {
            if (instance.WouldRepeat(key))
            {
                repeats.Add(key);
                continue;
            }
            return new SelectionOutcome(
                new ActivityMove(ActivityMoveKind.Question, key, text,
                    Rationale: "deterministic baseline: next unasked coarse-to-fine question",
                    Origin: MoveOrigin.Deterministic),
                null, repeats);
        }
        return SelectionOutcome.Failed("no-valid-question-available", repeats);
    }

    /// <summary>
    /// Deterministic validation of an UNTRUSTED move (model-proposed or otherwise). The
    /// model never edits state, binds answers, increments counters, declares completion,
    /// or overrides authorization — it only offers a move that this may refuse.
    /// </summary>
    public ValidationResult ValidateSelection(
        ActivityInstance instance, StrategyState state, ActivityMove move)
    {
        if (!instance.IsActive)
            return ValidationResult.Reject("activity-not-active");
        if (instance.AskerParticipantId is not { Length: > 0 })
            return ValidationResult.Reject("no-asker-role");
        if (string.IsNullOrWhiteSpace(move.StableKey) || !StableKey.IsMatch(move.StableKey))
            return ValidationResult.Reject("malformed-stable-key");
        if (string.IsNullOrWhiteSpace(move.Text) || move.Text.Length > 200)
            return ValidationResult.Reject("malformed-move-text");
        if (ControlishText.IsMatch(move.Text))
            return ValidationResult.Reject("instruction-shaped-text");

        if (move.Kind == ActivityMoveKind.Question)
        {
            if (instance.CurrentQuestionNumber > instance.QuestionLimit)
                return ValidationResult.Reject("question-limit-reached");
            if (instance.WouldRepeat(move.StableKey))
                return ValidationResult.Reject("repeated-question-key");
            // Hypotheses named by the proposal must not contradict recorded evidence.
            foreach (var h in move.Hypotheses ?? [])
                if (state.Hypotheses.Any(x => x.Excluded
                        && string.Equals(x.Label, h, StringComparison.OrdinalIgnoreCase)))
                    return ValidationResult.Reject("hypothesis-contradicts-evidence");
            return ValidationResult.Ok;
        }

        // A guess is legal only with something to go on: at least one answer bound, and
        // either enough confidence or the limit in sight.
        if (instance.Answers.Count == 0)
            return ValidationResult.Reject("guess-before-any-evidence");
        var nearLimit = instance.CurrentQuestionNumber >= instance.QuestionLimit - 2;
        var confident = (move.Confidence ?? 0) >= 0.6;
        return nearLimit || confident
            ? ValidationResult.Ok
            : ValidationResult.Reject("guess-premature");
    }

    public CompletionResult EvaluateCompletion(ActivityInstance instance, StrategyState state)
    {
        if (instance.Lifecycle == ActivityLifecycle.Abandoned)
            return new CompletionResult(true, ActivityLifecycle.Abandoned, "abandoned");
        if (instance.FinalGuessCorrect == true)
            return new CompletionResult(true, ActivityLifecycle.Completed, "correct-guess");
        if (instance.CurrentQuestionNumber > instance.QuestionLimit)
            return new CompletionResult(true, ActivityLifecycle.Completed, "question-limit-exhausted");
        return CompletionResult.Continue;
    }

    // ---- hypothesis maintenance --------------------------------------------------------

    /// <summary>
    /// Folds a validated move's declared hypotheses and the answer that followed into
    /// state: supported labels gain confidence, contradicted labels are excluded WITH the
    /// key that excluded them. Open-domain — labels are free text, never a fixed catalog,
    /// so any object at all remains a reachable endpoint.
    /// </summary>
    public static StrategyState Fold(
        StrategyState state, ActivityMove move, bool answer, IReadOnlyList<string>? excludes = null)
    {
        var hypotheses = state.Hypotheses.ToList();
        foreach (var label in move.Hypotheses ?? [])
            if (!hypotheses.Any(h => string.Equals(h.Label, label, StringComparison.OrdinalIgnoreCase)))
                hypotheses.Add(new Hypothesis(label, move.Confidence ?? 0.5));

        var killed = excludes ?? (answer ? [] : move.Hypotheses ?? []);
        hypotheses = hypotheses
            .Select(h => killed.Any(k => string.Equals(k, h.Label, StringComparison.OrdinalIgnoreCase))
                ? h with { Excluded = true, ExcludedByQuestionKey = move.StableKey }
                : h)
            .ToList();

        return state with
        {
            Hypotheses = hypotheses,
            Evidence = [.. state.Evidence, new EvidenceEntry(
                move.StableKey, answer,
                answer ? move.Hypotheses ?? [] : [],
                killed.ToList())],
        };
    }

    private static StrategyState CurrentState(ActivityInstance instance) => StrategyState.Empty;

    private static bool? Parse(string? text)
        => text is null ? null
            : Yes.IsMatch(text) ? true
            : No.IsMatch(text) ? false
            : null;

    private static readonly Regex StableKey = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>Proposal text that looks like instruction rather than a question/guess.</summary>
    private static readonly Regex ControlishText = new(
        @"(ignore (all )?previous|system prompt|you must|disregard|\[plan/|act =|override)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
