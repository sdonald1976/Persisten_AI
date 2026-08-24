namespace Companion.PlanV3;

/// <summary>
/// A real procedure instance (P5b, source 1). The activity is ACTIVATED explicitly from
/// the user's request and resolved by the procedure layer — a language model never
/// rediscovers on each turn that a game is happening. All state lives here, upstream;
/// native V3 receives only the selected question and a minimal frame.
/// </summary>
public sealed record ActivityInstance
{
    public required string InstanceId { get; init; }
    public required string ProcedureType { get; init; }
    public required int ProcedureVersion { get; init; }
    public required ActivityLifecycle Lifecycle { get; init; }

    /// <summary>Participant ids, not display names.</summary>
    public required string AskerParticipantId { get; init; }
    public required string AnswererParticipantId { get; init; }

    public required int QuestionLimit { get; init; }
    public required int CurrentQuestionNumber { get; init; }

    /// <summary>Asked questions by stable identity, so rephrasing cannot evade the check.</summary>
    public IReadOnlyList<AskedQuestion> AskedQuestions { get; init; } = [];

    /// <summary>Answers bound to the question identity they answered.</summary>
    public IReadOnlyList<AnswerBinding> Answers { get; init; } = [];

    public IReadOnlyList<string> EstablishedFacts { get; init; } = [];
    public IReadOnlyList<string> Exclusions { get; init; } = [];
    public IReadOnlyList<string> Candidates { get; init; } = [];

    public AskedQuestion? SelectedNextQuestion { get; init; }
    public string? FinalGuess { get; init; }
    public bool? FinalGuessCorrect { get; init; }

    /// <summary>Set when selection failed: a diagnosed procedure failure, never silence.</summary>
    public string? SelectionFailureReason { get; init; }

    public bool IsActive => Lifecycle == ActivityLifecycle.Active;

    /// <summary>Stable-identity repeat check: the question KEY decides, not the wording.</summary>
    public bool WouldRepeat(string questionKey)
        => AskedQuestions.Any(q => string.Equals(q.Key, questionKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Upstream selection: the first candidate question whose key is neither already
    /// asked nor already settled by an established fact or exclusion. Returns the
    /// instance with the selection recorded, or with a diagnosed failure reason — never
    /// a silently ordinary turn.
    /// </summary>
    public ActivityInstance SelectNext(IEnumerable<AskedQuestion> pool)
    {
        if (Lifecycle != ActivityLifecycle.Active)
            return this with { SelectedNextQuestion = null, SelectionFailureReason = "activity-not-active" };
        if (CurrentQuestionNumber > QuestionLimit)
            return this with { SelectedNextQuestion = null, SelectionFailureReason = "question-limit-reached" };

        var settled = new HashSet<string>(
            EstablishedFacts.Concat(Exclusions), StringComparer.OrdinalIgnoreCase);
        var choice = pool.FirstOrDefault(q => !WouldRepeat(q.Key) && !settled.Contains(q.Key));
        return choice is null
            ? this with { SelectedNextQuestion = null, SelectionFailureReason = "no-valid-question-available" }
            : this with { SelectedNextQuestion = choice, SelectionFailureReason = null };
    }

    /// <summary>Records the answer against the question it actually answered.</summary>
    public ActivityInstance RecordAnswer(string questionKey, bool answer)
        => this with
        {
            Answers = [.. Answers, new AnswerBinding(questionKey, answer)],
            CurrentQuestionNumber = CurrentQuestionNumber + 1,
        };

    public ActivityInstance Ask(AskedQuestion question)
        => this with { AskedQuestions = [.. AskedQuestions, question], SelectedNextQuestion = null };
}

public enum ActivityLifecycle { Proposed, Active, Completed, Abandoned }

/// <summary>A question with a stable key: rephrasing does not create a new question.</summary>
public sealed record AskedQuestion(string Key, string Text);

public sealed record AnswerBinding(string QuestionKey, bool Answer);

/// <summary>
/// Procedure contributor over a real instance (P5b). Contributes the selected question
/// and a minimal frame; when selection failed, contributes NOTHING and reports the
/// diagnosed reason so the turn cannot pretend the activity vanished.
/// </summary>
public sealed class ActivityProcedureContributor(ActivityInstance? instance) : IPlanV3Contributor
{
    public string SourceId => "procedure";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        if (instance is null || !instance.IsActive)
            return PlanContributionResult.Empty;

        if (instance.SelectionFailureReason is { } failure)
            return PlanContributionResult.Failed($"procedure-selection-failed:{failure}");

        var items = new List<ProposedItem>();
        if (instance.SelectedNextQuestion is { } q)
            items.Add(new ProposedItem
            {
                LocalId = $"q-{q.Key}",
                Type = "activity-question",
                Category = RenderCategory.clarify,
                ProposedPolicy = ExpressionPolicy.ask_required,
                Text = q.Text,
                Provenance = new Provenance(Origin: "derived", EvidenceRef: $"activity:{instance.InstanceId}"),
            });

        var asker = instance.AskerParticipantId == context.CompanionParticipantId ? "Ava asks" : "Scott asks";
        items.Add(new ProposedItem
        {
            LocalId = "frame",
            Type = "activity-state",
            Category = RenderCategory.state,
            ProposedPolicy = ExpressionPolicy.background_only,
            Text = $"{instance.ProcedureType}: {asker}; question {instance.CurrentQuestionNumber} of {instance.QuestionLimit}.",
            Provenance = new Provenance(Origin: "derived", EvidenceRef: $"activity:{instance.InstanceId}"),
        });
        return new PlanContributionResult(items);
    }
}
