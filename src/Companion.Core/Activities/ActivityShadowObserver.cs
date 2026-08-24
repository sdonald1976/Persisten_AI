using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Activities;

/// <summary>
/// The turn-path call site for activity shadow (Source 1b, final). Consumes an IMMUTABLE
/// snapshot taken after the displayed response is finalized, maintains the two natural
/// branches separately, and persists labeled rows. Never delays or alters a displayed
/// reply; every failure is content-safe and user-invisible.
/// </summary>
public sealed class ActivityShadowObserver(IActivityBranchStore store)
{
    /// <summary>Immutable per-turn snapshot; the observer can reach nothing else.</summary>
    public sealed record TurnSnapshot(
        Guid TraceId,
        string UserId,
        Guid ConversationId,
        Guid MessageId,
        string UserMessage,
        string DisplayedReply,
        string DisplayedRenderer,
        ActivityInstance? Instance,
        StrategyState State,
        ActivityMove? NativeSelectedMove,
        string? NativeSelectionFailure,
        bool SensitiveTurn,
        DateTimeOffset At);

    public sealed record ObservationResult(
        bool Observed,
        string? ObservedBranchId,
        string? CounterfactualBranchId,
        string DisplayedMoveState,          // resolved key | "displayed-move-unresolved"
        bool NextInputBindable,
        string? Failure);

    /// <summary>
    /// Conservative displayed-question identification. Exactly one interrogative sentence
    /// in the displayed reply resolves; zero, several, or an unmatchable one records
    /// `displayed-move-unresolved` — an identity is never invented.
    /// </summary>
    public static (string? Key, string State) ResolveDisplayedMove(
        string displayedReply, ActivityInstance? instance)
    {
        var questions = Regex.Matches(displayedReply ?? "", @"[^.!?\n]*\?")
            .Select(m => m.Value.Trim())
            .Where(q => q.Length > 1)
            .ToList();

        if (questions.Count != 1)
            return (null, "displayed-move-unresolved");
        if (instance?.PendingMove is not { } pending)
            return (null, "displayed-move-unresolved");

        // The single displayed question must actually correspond to the pending move:
        // distinctive-token overlap, not fuzzy vibes.
        var displayed = Tokens(questions[0]);
        var expected = Tokens(pending.Text);
        if (expected.Count == 0)
            return (null, "displayed-move-unresolved");
        var overlap = expected.Count(t => displayed.Contains(t)) / (double)expected.Count;
        return overlap >= 0.6
            ? (pending.StableKey, pending.StableKey)
            : (null, "displayed-move-unresolved");
    }

    private static HashSet<string> Tokens(string text)
        => new(Regex.Matches(text.ToLowerInvariant(), @"[a-z]{4,}").Select(m => m.Value));

    /// <summary>
    /// Records the turn's two natural branches. ProductionObserved carries the reply that
    /// was actually displayed (bindable only when one move resolved); CounterfactualNative
    /// carries the native move nobody saw, never bindable, naming its parent and branch
    /// point. Both are separate rows.
    /// </summary>
    public async Task<ObservationResult> ObserveNaturalAsync(
        TurnSnapshot snapshot, CancellationToken ct = default)
    {
        try
        {
            if (snapshot.Instance is not { } instance || !instance.IsActive)
                return new ObservationResult(false, null, null, "no-active-activity", false, null);

            var (resolvedKey, state) = ResolveDisplayedMove(snapshot.DisplayedReply, instance);
            var bindable = resolvedKey is not null;
            var retention = snapshot.SensitiveTurn ? "volatile_turn_only" : "no_training";

            var observedBranchId = $"{instance.InstanceId}:observed";
            var observedMove = resolvedKey is null
                ? null
                : new BranchMove
                {
                    BranchId = observedBranchId,
                    MoveId = $"{observedBranchId}:{resolvedKey}",
                    Move = instance.PendingMove!,
                    Disposition = MoveDisposition.ObservedDisplayed,
                    DisplayedRenderer = snapshot.DisplayedRenderer,
                    DisplayedQuestionId = resolvedKey,
                    At = snapshot.At,
                };

            var observedRecord = BuildRecord(snapshot, instance, observedBranchId,
                BranchKind.ProductionObserved, "natural-observed",
                observedMove is null ? [] : [observedMove],
                retention, parent: null, branchPoint: null, displayedState: state);
            observedRecord.Version = (await store.GetAsync(observedBranchId, ct))?.Version ?? 1;
            await store.UpsertAsync(observedRecord, $"observed:{snapshot.MessageId}", ct);

            string? counterfactualId = null;
            if (snapshot.NativeSelectedMove is { } native)
            {
                counterfactualId = $"{instance.InstanceId}:cf:{instance.CurrentQuestionNumber}";
                var cfMove = new BranchMove
                {
                    BranchId = counterfactualId,
                    MoveId = $"{counterfactualId}:{native.StableKey}",
                    Move = native,
                    Disposition = MoveDisposition.CounterfactualNotDisplayed,
                    DisplayedRenderer = null,
                    DisplayedQuestionId = null,
                    At = snapshot.At,
                };
                var cfRecord = BuildRecord(snapshot, instance, counterfactualId,
                    BranchKind.CounterfactualNative, "natural-counterfactual", [cfMove], retention,
                    parent: observedBranchId, branchPoint: instance.CurrentQuestionNumber,
                    displayedState: "counterfactual-not-displayed");
                cfRecord.Version = (await store.GetAsync(counterfactualId, ct))?.Version ?? 1;
                await store.UpsertAsync(cfRecord, $"counterfactual:{snapshot.MessageId}", ct);
            }

            return new ObservationResult(true, observedBranchId, counterfactualId, state, bindable, null);
        }
        catch (Exception ex)
        {
            // Content-safe and user-invisible: the type only, never the content.
            return new ObservationResult(false, null, null, "failed", false, ex.GetType().Name);
        }
    }

    /// <summary>Persists one simulated branch through the SAME store path natural rows use.</summary>
    public Task<BranchWriteResult> RecordSimulatedAsync(
        TurnSnapshot snapshot, ActivityInstance instance, IReadOnlyList<BranchMove> moves,
        string branchId, string idempotencyKey, string retention = "no_training",
        int expectedVersion = 1, CancellationToken ct = default)
    {
        var record = BuildRecord(snapshot, instance, branchId, BranchKind.Simulated, "simulated",
            moves, retention, parent: null, branchPoint: null, displayedState: "simulated-displayed");
        record.Version = expectedVersion;
        return store.UpsertAsync(record, idempotencyKey, ct);
    }

    private static ActivityBranchRecord BuildRecord(
        TurnSnapshot snapshot, ActivityInstance instance, string branchId, BranchKind kind,
        string label, IReadOnlyList<BranchMove> moves, string retention,
        string? parent, int? branchPoint, string displayedState)
    {
        var terminal = instance.Lifecycle is ActivityLifecycle.Completed or ActivityLifecycle.Abandoned;
        return new ActivityBranchRecord
        {
            UserId = snapshot.UserId,
            ConversationId = snapshot.ConversationId,
            InstanceId = instance.InstanceId,
            BranchId = branchId,
            ParentBranchId = parent,
            BranchPointQuestionNumber = branchPoint,
            BranchKind = kind.ToString(),
            Label = label,
            ProcedureDefinitionId = instance.ProcedureId,
            ActivityType = instance.ActivityType,
            StrategyVersion = instance.StrategyVersion,
            Lifecycle = instance.Lifecycle.ToString(),
            Version = 1,
            QuestionLimit = instance.QuestionLimit,
            CurrentQuestionNumber = instance.CurrentQuestionNumber,
            MovesJson = JsonSerializer.Serialize(moves.Select(m => new
            {
                key = m.Move.StableKey,
                text = m.Move.Text,
                kind = m.Move.Kind.ToString(),
                origin = m.Move.Origin.ToString(),
                disposition = Disposition(m.Disposition),
                displayedRenderer = m.DisplayedRenderer,
                displayedQuestionId = m.DisplayedQuestionId,
                bindable = m.NextInputBindable,
            })),
            AnswerBindingsJson = JsonSerializer.Serialize(instance.Answers.Select(a => new
            {
                questionKey = a.QuestionKey, answer = a.Answer,
            })),
            HypothesesJson = JsonSerializer.Serialize(snapshot.State.Hypotheses.Select(h => new
            {
                label = h.Label, confidence = h.Confidence,
                excluded = h.Excluded, excludedBy = h.ExcludedByQuestionKey,
            })),
            FinalGuess = instance.FinalGuess,
            FinalGuessCorrect = instance.FinalGuessCorrect,
            ActivationEvidence = instance.ActivationEvidence,
            Retention = retention,
            ActivatedAt = instance.ActivatedAt,
            UpdatedAt = snapshot.At,
            TerminalAt = terminal ? snapshot.At : null,
        };
    }

    private static string Disposition(MoveDisposition d) => d switch
    {
        MoveDisposition.ObservedDisplayed => "observed_displayed",
        MoveDisposition.SimulatedDisplayed => "simulated_displayed",
        _ => "counterfactual_not_displayed",
    };
}
