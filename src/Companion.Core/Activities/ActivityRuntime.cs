namespace Companion.Core.Activities;

/// <summary>
/// The proposer boundary (§A5). NOT run-1c: run-1c is the mouth and renders an
/// already-selected move. A separately configured reasoning endpoint returns an UNTRUSTED
/// structured proposal; deterministic validation decides. Replaceable without touching the
/// runtime or the ResponsePlan protocol.
/// </summary>
public interface IActivityMoveProposer
{
    /// <summary>Identity traced on every proposal: model, prompt version, temperature, seed.</summary>
    ProposerIdentity Identity { get; }

    /// <summary>Receives the MINIMUM projection of the ledger — never the whole instance.</summary>
    Task<ProposalResult> ProposeAsync(SelectionProjection projection, CancellationToken ct = default);
}

public sealed record ProposerIdentity(
    string Provider, string Model, string PromptVersion, double Temperature, int? Seed);

/// <summary>The minimum a selector needs: asked keys, bound answers, live hypotheses, position.</summary>
public sealed record SelectionProjection(
    string ActivityType,
    int QuestionNumber,
    int QuestionLimit,
    IReadOnlyList<string> AskedKeys,
    IReadOnlyList<(string Key, bool Answer)> Answers,
    IReadOnlyList<string> LiveHypotheses);

/// <summary>Raw structured proposal plus trace data; retained under no_training rules.</summary>
public sealed record ProposalResult(
    ActivityMove? Move, string? RawJson, long LatencyMs, string? Error);

/// <summary>Content-safe record of one selection attempt.</summary>
public sealed record SelectionAttempt(
    int Attempt, MoveOrigin Origin, bool Accepted, string? RejectionReason, long LatencyMs);

public sealed record SelectionSession(
    ActivityMove? Move,
    string? FailureReason,
    IReadOnlyList<SelectionAttempt> Attempts,
    ProposerIdentity? ProposerUsed);

/// <summary>
/// The generic activity runtime (Source 1b §A1). Owns lifecycle, identities, transactions,
/// idempotency, and authority; knows no activity's rules. Every mutation returns a new
/// instance — the caller persists it, so the runtime itself stays pure and testable.
/// </summary>
public sealed class ActivityRuntime(IActivityStrategy strategy, IActivityMoveProposer? proposer = null,
    int maxProposalRetries = 2)
{
    public IActivityStrategy Strategy => strategy;

    /// <summary>
    /// Explicit activation (§3 of the brief). Never inferred from topic similarity or a
    /// model casually mentioning a game: the caller must supply activation evidence.
    /// </summary>
    public (ActivityInstance Instance, StrategyState State) Activate(
        ActivityDefinition definition,
        string instanceId, string userId, Guid conversationId,
        DateTimeOffset now, string activationEvidence)
    {
        if (string.IsNullOrWhiteSpace(activationEvidence))
            throw new ArgumentException("activation requires evidence", nameof(activationEvidence));

        var instance = new ActivityInstance
        {
            InstanceId = instanceId,
            ActivityType = definition.ActivityType,
            StrategyVersion = strategy.Version,
            Lifecycle = ActivityLifecycle.Active,
            ProcedureId = definition.ProcedureId,
            UserId = userId,
            ConversationId = conversationId,
            AskerParticipantId = definition.AskerParticipantId,
            AnswererParticipantId = definition.AnswererParticipantId,
            QuestionLimit = definition.QuestionLimit,
            CurrentQuestionNumber = 1,
            ActivatedAt = now,
            ActivationEvidence = activationEvidence,
        };
        return (instance, strategy.Initialize(definition));
    }

    /// <summary>Transactional, idempotent input application; completion evaluated after.</summary>
    public TransitionResult ApplyInput(ActivityInstance instance, StrategyState state, ActivityInput input)
    {
        var result = strategy.ApplyInput(instance, input);
        if (!result.Applied)
            return result;

        var completion = strategy.EvaluateCompletion(result.Instance, result.State);
        return completion is { Complete: true, Lifecycle: { } lifecycle }
            ? result with { Instance = result.Instance with { Lifecycle = lifecycle } }
            : result;
    }

    /// <summary>
    /// Hybrid selection (§A3): ask the proposer, validate deterministically, retry a
    /// bounded number of times recording every rejection, then fall back to the
    /// deterministic baseline — whose own failure is a diagnosed selection failure, never
    /// an ordinary turn.
    /// </summary>
    public async Task<SelectionSession> SelectAsync(
        ActivityInstance instance, StrategyState state, CancellationToken ct = default)
    {
        var attempts = new List<SelectionAttempt>();

        if (proposer is not null)
        {
            var projection = Project(instance, state);
            for (var attempt = 1; attempt <= Math.Max(1, maxProposalRetries + 1); attempt++)
            {
                ProposalResult proposal;
                try
                {
                    proposal = await proposer.ProposeAsync(projection, ct);
                }
                catch (Exception ex)
                {
                    attempts.Add(new SelectionAttempt(attempt, MoveOrigin.ModelProposal, false,
                        $"proposer-failed:{ex.GetType().Name}", 0));
                    break;
                }

                if (proposal.Move is not { } move)
                {
                    attempts.Add(new SelectionAttempt(attempt, MoveOrigin.ModelProposal, false,
                        proposal.Error ?? "no-proposal", proposal.LatencyMs));
                    continue;
                }

                var validation = strategy.ValidateSelection(instance, state, move);
                attempts.Add(new SelectionAttempt(attempt, MoveOrigin.ModelProposal,
                    validation.Valid, validation.Reason, proposal.LatencyMs));
                if (validation.Valid)
                    return new SelectionSession(move with { Origin = MoveOrigin.ModelProposal },
                        null, attempts, proposer.Identity);
            }
        }

        var fallback = strategy.SelectNext(instance, state);
        if (fallback.Move is { } deterministic)
        {
            // The baseline is trusted code, but it is validated too — one gate for all moves.
            var validation = strategy.ValidateSelection(instance, state, deterministic);
            attempts.Add(new SelectionAttempt(attempts.Count + 1, MoveOrigin.Deterministic,
                validation.Valid, validation.Reason, 0));
            return validation.Valid
                ? new SelectionSession(deterministic, null, attempts, proposer?.Identity)
                : new SelectionSession(null, validation.Reason ?? "fallback-invalid", attempts, proposer?.Identity);
        }

        attempts.Add(new SelectionAttempt(attempts.Count + 1, MoveOrigin.Deterministic,
            false, fallback.FailureReason, 0));
        return new SelectionSession(null, fallback.FailureReason ?? "selection-failed",
            attempts, proposer?.Identity);
    }

    /// <summary>Records the selected move as pending and asked — deterministic numbering.</summary>
    public ActivityInstance RecordSelectedMove(ActivityInstance instance, ActivityMove move)
        => move.Kind == ActivityMoveKind.Question
            ? instance with
            {
                PendingMove = move,
                AskedQuestions = [.. instance.AskedQuestions,
                    new AskedQuestion(move.StableKey, move.Text, instance.CurrentQuestionNumber)],
            }
            : instance with { PendingMove = move, FinalGuess = move.Text };

    /// <summary>The minimum projection handed across the trust boundary (§A5, §7).</summary>
    public static SelectionProjection Project(ActivityInstance instance, StrategyState state)
        => new(
            instance.ActivityType,
            instance.CurrentQuestionNumber,
            instance.QuestionLimit,
            instance.AskedQuestions.Select(q => q.Key).ToList(),
            instance.Answers.Select(a => (a.QuestionKey, a.Answer)).ToList(),
            state.Live.Select(h => h.Label).ToList());
}
