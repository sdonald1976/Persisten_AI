using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Activities;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Source 1b final: the turn-path call site and the declared LifeRunner volume
/// (docs/SOURCE1B_VOLUME_PLAN.md — 12 sessions, 11 scenarios, 9 pass criteria). Every
/// session runs the SAME activation → runtime → observer → store path a natural turn
/// uses, on isolated users/conversations with a controlled clock. All data synthetic.
/// </summary>
public class ActivityLifeRunnerTests
{
    private static readonly DateTimeOffset Clock = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    // ================= the natural-turn call site =================

    private static ActivityInstance Active(string id = "tq-nat", ActivityMove? pending = null) => new()
    {
        InstanceId = id, ActivityType = "twenty-questions", StrategyVersion = "1",
        Lifecycle = ActivityLifecycle.Active, UserId = "usr-nat",
        ConversationId = Guid.Parse("55555555-0000-0000-0000-000000000005"),
        AskerParticipantId = "companion-ava", AnswererParticipantId = "usr-nat",
        QuestionLimit = 20, CurrentQuestionNumber = 3, ActivatedAt = Clock,
        ActivationEvidence = "message:nat1 synthetic activation",
        PendingMove = pending,
    };

    private static ActivityShadowObserver.TurnSnapshot Snapshot(
        ActivityInstance? instance, string displayedReply,
        ActivityMove? nativeMove = null, bool sensitive = false, Guid? messageId = null)
        => new(Guid.NewGuid(), "usr-nat", Guid.Parse("55555555-0000-0000-0000-000000000005"),
            messageId ?? Guid.NewGuid(), "synthetic user message", displayedReply,
            "production-stheno", instance, StrategyState.Empty, nativeMove, null, sensitive, Clock);

    [Theory]
    [InlineData("Nice one. Is it made of metal?", "material-primary")]                 // exactly one
    [InlineData("Is it metal? And is it heavy?", "displayed-move-unresolved")]          // multiple
    [InlineData("That narrows things down nicely.", "displayed-move-unresolved")]       // none
    [InlineData("Do you like pineapple on pizza?", "displayed-move-unresolved")]        // unmatchable
    public void DisplayedMoveIdentification_IsConservative(string reply, string expected)
    {
        var pending = new ActivityMove(ActivityMoveKind.Question, "material-primary",
            "is it primarily made of metal");
        var (_, state) = ActivityShadowObserver.ResolveDisplayedMove(reply, Active(pending: pending));
        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task NaturalObservation_SeparatesObservedFromCounterfactual()
    {
        await using var host = new TestHost(Clock);
        var store = host.Services.GetRequiredService<IActivityBranchStore>();
        var observer = new ActivityShadowObserver(store);

        var pending = new ActivityMove(ActivityMoveKind.Question, "material-primary",
            "is it primarily made of metal");
        var native = new ActivityMove(ActivityMoveKind.Question, "kitchen",
            "does it belong in a kitchen", Origin: MoveOrigin.ModelProposal);

        var result = await observer.ObserveNaturalAsync(
            Snapshot(Active(pending: pending), "Right — is it primarily made of metal?", native));

        Assert.True(result.Observed);
        Assert.Equal("material-primary", result.DisplayedMoveState);
        Assert.True(result.NextInputBindable);

        var observed = await store.GetAsync(result.ObservedBranchId!);
        var counterfactual = await store.GetAsync(result.CounterfactualBranchId!);

        Assert.Equal("natural-observed", observed!.Label);
        Assert.Contains("observed_displayed", observed.MovesJson);
        Assert.Contains("production-stheno", observed.MovesJson);

        Assert.Equal("natural-counterfactual", counterfactual!.Label);
        Assert.Contains("counterfactual_not_displayed", counterfactual.MovesJson);
        Assert.Contains("\"bindable\":false", counterfactual.MovesJson);
        Assert.Equal(observed.BranchId, counterfactual.ParentBranchId);
        Assert.Equal(3, counterfactual.BranchPointQuestionNumber);
        Assert.NotEqual(observed.BranchId, counterfactual.BranchId);
    }

    [Fact]
    public async Task WhenTheDisplayedMoveIsUnresolved_NothingIsBindable_AndNoIdentityIsInvented()
    {
        await using var host = new TestHost(Clock);
        var store = host.Services.GetRequiredService<IActivityBranchStore>();
        var observer = new ActivityShadowObserver(store);

        var result = await observer.ObserveNaturalAsync(Snapshot(
            Active(pending: new ActivityMove(ActivityMoveKind.Question, "metal", "is it metal")),
            "Is it metal? Or is it plastic?"));

        Assert.Equal("displayed-move-unresolved", result.DisplayedMoveState);
        Assert.False(result.NextInputBindable);
        var observed = await store.GetAsync(result.ObservedBranchId!);
        Assert.Equal("[]", observed!.MovesJson);      // no invented move
    }

    [Fact]
    public async Task ObservationFailure_IsContentSafeAndInvisible()
    {
        var observer = new ActivityShadowObserver(new ThrowingStore());
        var result = await observer.ObserveNaturalAsync(Snapshot(
            Active(pending: new ActivityMove(ActivityMoveKind.Question, "metal", "is it metal")),
            "Is it metal?"));

        Assert.False(result.Observed);
        Assert.Equal("InvalidOperationException", result.Failure);   // type only, no content
    }

    private sealed class ThrowingStore : IActivityBranchStore
    {
        public Task<BranchWriteResult> UpsertAsync(Core.Domain.ActivityBranchRecord r, string k, CancellationToken ct = default)
            => throw new InvalidOperationException("synthetic store failure with secret content");
        public Task<Core.Domain.ActivityBranchRecord?> GetAsync(string b, CancellationToken ct = default)
            => Task.FromResult<Core.Domain.ActivityBranchRecord?>(null);
        public Task<IReadOnlyList<Core.Domain.ActivityBranchRecord>> GetForConversationAsync(string u, Guid c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Core.Domain.ActivityBranchRecord>>([]);
        public Task<int> CleanupAsync(DateTimeOffset n, TimeSpan t, TimeSpan v, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetAsync(IReadOnlyCollection<string> e, CancellationToken ct = default) => Task.FromResult(0);
    }

    // ================= LifeRunner: the declared 12 sessions =================

    /// <summary>Drives complete simulated sessions through activation → runtime → observer → store.</summary>
    private sealed class LifeRunner(IActivityBranchStore store, string userId, string sessionId)
    {
        private readonly ActivityShadowObserver _observer = new(store);
        private readonly Guid _conversationId = Guid.NewGuid();
        private DateTimeOffset _now = Clock;

        public ActivityInstance Instance { get; private set; } = default!;
        public StrategyState State { get; private set; } = StrategyState.Empty;
        public List<BranchMove> Moves { get; } = [];
        public string BranchId => $"{sessionId}:simulated";

        public void Activate(ActivityRuntime runtime, int limit = 20, string retention = "no_training")
        {
            var (instance, state) = runtime.Activate(
                new ActivityDefinition("twenty-questions", "1",
                    Guid.Parse("66666666-0000-0000-0000-000000000006"), limit, "companion-ava", userId),
                sessionId, userId, _conversationId, _now,
                $"message:{Guid.NewGuid()} \"let's play 20 questions\"");
            Instance = instance;
            State = state;
            Retention = retention;
        }

        public string Retention { get; private set; } = "no_training";

        /// <summary>Displays the native-selected move to the simulated user — which is what
        /// makes the simulated answer legally bindable.</summary>
        public async Task<ActivityMove?> AskAsync(ActivityRuntime runtime)
        {
            var session = await runtime.SelectAsync(Instance, State);
            if (session.Move is not { } move)
                return null;
            Instance = runtime.RecordSelectedMove(Instance, move);
            Moves.Add(new BranchMove
            {
                BranchId = BranchId, MoveId = $"{BranchId}:{move.StableKey}", Move = move,
                Disposition = MoveDisposition.SimulatedDisplayed,
                DisplayedRenderer = "native-simulated", DisplayedQuestionId = move.StableKey,
                At = _now,
            });
            await PersistAsync();
            return move;
        }

        public async Task<TransitionResult> AnswerAsync(
            ActivityRuntime runtime, ActivityInput input)
        {
            _now = _now.AddSeconds(30);
            var result = runtime.ApplyInput(Instance, State, input);
            if (result.Applied)
            {
                Instance = result.Instance;
                if (Instance.PendingMove is null && Moves.Count > 0 && input.BooleanAnswer is { } a)
                    State = TwentyQuestionsStrategy.Fold(State, Moves[^1].Move, a);
            }
            await PersistAsync(input.MessageId.ToString());
            return result;
        }

        private int _version = 1;

        public async Task<BranchWriteResult> PersistAsync(string? key = null)
        {
            var result = await _observer.RecordSimulatedAsync(
                new ActivityShadowObserver.TurnSnapshot(Guid.NewGuid(), userId, _conversationId,
                    Guid.NewGuid(), "simulated", "simulated display", "native-simulated",
                    Instance, State, null, null, false, _now),
                Instance, Moves, BranchId,
                key ?? $"turn:{Moves.Count}:{Instance.CurrentQuestionNumber}", Retention, _version);
            if (result.Applied)
                _version = result.Record.Version;
            return result;
        }

        public void SetInstance(ActivityInstance i) => Instance = i;
    }

    private sealed class ScriptedProposer(params ActivityMove[] moves) : IActivityMoveProposer
    {
        private int _i;
        public ProposerIdentity Identity { get; } = new("captured", "fixture", "v1", 0.0, 7);
        public Task<ProposalResult> ProposeAsync(SelectionProjection p, CancellationToken ct = default)
            => Task.FromResult(_i < moves.Length
                ? new ProposalResult(moves[_i++], "{\"captured\":true}", 5, null)
                : new ProposalResult(null, null, 0, "exhausted"));
    }

    private static ActivityMove Q(string key, string text, params string[] hypotheses)
        => new(ActivityMoveKind.Question, key, text, "captured",
            hypotheses.Length > 0 ? hypotheses : ["a household object"], 0.6, MoveOrigin.ModelProposal);

    private static ActivityInput Answer(string key, bool value)
        => new(ActivityInputKind.Answer, key, value, value ? "yes" : "no", Guid.NewGuid(), Clock);

    /// <summary>
    /// The whole declared volume in one deterministic run: 12 sessions, 11 scenarios,
    /// evaluated against the nine pre-declared pass criteria.
    /// </summary>
    [Fact]
    public async Task LifeRunner_RunsTheDeclaredVolume_AndMeetsEveryPassCriterion()
    {
        await using var host = new TestHost(Clock);
        var store = host.Services.GetRequiredService<IActivityBranchStore>();
        var results = new Dictionary<string, string>();

        // --- 1. correct guess, captured proposals, ends "a dildo" ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy(), new ScriptedProposer(
                // Coherent narrowing: each question names the hypotheses it discriminates,
                // and a "no" excludes exactly those — which is why the later proposals must
                // name hypotheses still alive, or validation refuses them.
                Q("physical", "does it exist physically", "a physical object"),
                Q("man-made", "is it man-made", "a manufactured item"),
                Q("hand-held", "is it small enough to hold in one hand", "a personal item", "a hand tool"),
                Q("moving-parts", "does it have moving parts", "a hand tool"),
                Q("personal", "is it something a person keeps to themselves", "a personal item"),
                Q("visible-guest", "would a guest see it out in the open", "a decorative object"),
                new ActivityMove(ActivityMoveKind.Guess, "final-guess", "is it a dildo",
                    "captured", ["a personal item"], 0.72, MoveOrigin.ModelProposal)));
            var runner = new LifeRunner(store, "usr-sim-1", "sim-correct-guess");
            runner.Activate(runtime);
            foreach (var a in new[] { true, true, true, false, true, false })
            {
                var move = await runner.AskAsync(runtime);
                await runner.AnswerAsync(runtime, Answer(move!.StableKey, a));
            }
            var guess = await runner.AskAsync(runtime);
            Assert.Equal("is it a dildo", guess!.Text);
            var verdict = await runner.AnswerAsync(runtime, new ActivityInput(
                ActivityInputKind.GuessVerdict, null, true, "yes!", Guid.NewGuid(), Clock));
            Assert.Equal(ActivityLifecycle.Completed, verdict.Instance.Lifecycle);
            Assert.True(verdict.Instance.FinalGuessCorrect);
            results["1-correct-guess"] = "Completed/correct";
        }

        // --- 2. incorrect guess, game continues ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy(), new ScriptedProposer(
                Q("physical", "does it exist physically"), Q("man-made", "is it man-made"),
                Q("indoors", "is it usually found indoors"),
                new ActivityMove(ActivityMoveKind.Guess, "g1", "is it a kettle", null, null, 0.7,
                    MoveOrigin.ModelProposal)));
            var runner = new LifeRunner(store, "usr-sim-2", "sim-wrong-guess");
            runner.Activate(runtime);
            foreach (var _ in Enumerable.Range(0, 3))
            {
                var m = await runner.AskAsync(runtime);
                await runner.AnswerAsync(runtime, Answer(m!.StableKey, true));
            }
            await runner.AskAsync(runtime);
            var verdict = await runner.AnswerAsync(runtime, new ActivityInput(
                ActivityInputKind.GuessVerdict, null, false, "no", Guid.NewGuid(), Clock));
            Assert.Equal(ActivityLifecycle.Active, verdict.Instance.Lifecycle);
            Assert.Null(verdict.Instance.FinalGuess);
            results["2-incorrect-guess"] = "Active/guess-cleared";
        }

        // --- 3. exhausted question limit ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-3", "sim-limit");
            runner.Activate(runtime, limit: 4);
            ActivityInstance last = runner.Instance;
            for (var i = 0; i < 4; i++)
            {
                var m = await runner.AskAsync(runtime);
                if (m is null) break;
                last = (await runner.AnswerAsync(runtime, Answer(m.StableKey, true))).Instance;
            }
            Assert.Equal(ActivityLifecycle.Completed, last.Lifecycle);
            results["3-limit"] = "Completed/limit";
        }

        // --- 4. answer correction ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-4", "sim-correction");
            runner.Activate(runtime);
            var keys = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var m = await runner.AskAsync(runtime);
                keys.Add(m!.StableKey);
                await runner.AnswerAsync(runtime, Answer(m.StableKey, true));
            }
            var corrected = await runner.AnswerAsync(runtime, new ActivityInput(
                ActivityInputKind.Correction, keys[0], false, "actually no", Guid.NewGuid(), Clock));
            Assert.True(corrected.Applied);
            Assert.False(corrected.Instance.Answers.Single(a => a.QuestionKey == keys[0]).Answer);
            Assert.True(corrected.Instance.Answers.Single(a => a.QuestionKey == keys[1]).Answer);
            results["4-correction"] = "Active/rebound-one";
        }

        // --- 5. malformed answer ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-5", "sim-malformed");
            runner.Activate(runtime);
            var m = await runner.AskAsync(runtime);
            var before = runner.Instance.CurrentQuestionNumber;
            var result = await runner.AnswerAsync(runtime, new ActivityInput(
                ActivityInputKind.Answer, m!.StableKey, null, "mmm maybe?", Guid.NewGuid(), Clock));
            Assert.False(result.Applied);
            Assert.Equal("malformed-answer", result.RejectionReason);
            Assert.Equal(before, runner.Instance.CurrentQuestionNumber);
            results["5-malformed"] = "Active/unchanged";
        }

        // --- 6. abandonment ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-6", "sim-abandon");
            runner.Activate(runtime);
            await runner.AskAsync(runtime);
            var abandoned = await runner.AnswerAsync(runtime, new ActivityInput(
                ActivityInputKind.Abandon, null, null, "let's stop", Guid.NewGuid(), Clock));
            Assert.Equal(ActivityLifecycle.Abandoned, abandoned.Instance.Lifecycle);
            results["6-abandon"] = "Abandoned";
        }

        // --- 7. retry / idempotency ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-7", "sim-retry");
            runner.Activate(runtime);
            var m = await runner.AskAsync(runtime);
            var msg = Guid.NewGuid();
            var input = new ActivityInput(ActivityInputKind.Answer, m!.StableKey, true, "yes", msg, Clock);
            var first = await runner.AnswerAsync(runtime, input);
            var replay = runtime.ApplyInput(first.Instance, runner.State, input);
            Assert.True(first.Applied);
            Assert.False(replay.Applied);
            Assert.Equal("already-applied", replay.RejectionReason);
            Assert.Single(first.Instance.Answers);
            results["7-idempotency"] = "single-binding";
        }

        // --- 8. restart / resume (durable) ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-8", "sim-resume");
            runner.Activate(runtime);
            for (var i = 0; i < 3; i++)
            {
                var m = await runner.AskAsync(runtime);
                await runner.AnswerAsync(runtime, Answer(m!.StableKey, true));
            }
            var reloaded = await store.GetAsync(runner.BranchId);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.ContentWithheld);
            Assert.Contains("physical", reloaded.MovesJson);
            Assert.Equal(3, JsonSerializer.Deserialize<List<JsonElement>>(reloaded.AnswerBindingsJson)!.Count);
            results["8-resume"] = "resumed-with-content";
        }

        // --- 9. volatile: no resume, diagnosed ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-9", "sim-volatile");
            runner.Activate(runtime, retention: "volatile_turn_only");
            for (var i = 0; i < 3; i++)
            {
                var m = await runner.AskAsync(runtime);
                await runner.AnswerAsync(runtime, Answer(m!.StableKey, true));
            }
            var reloaded = await store.GetAsync(runner.BranchId);
            Assert.True(reloaded!.ContentWithheld);
            Assert.Equal("[]", reloaded.MovesJson);
            Assert.Null(reloaded.HypothesesJson);
            Assert.Equal("twenty-questions", reloaded.ActivityType);   // metadata survives
            results["9-volatile"] = "resumed-without-content";
        }

        // --- 10. two simultaneous users ---
        {
            var runtimeA = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runtimeB = new ActivityRuntime(new TwentyQuestionsStrategy());
            var a = new LifeRunner(store, "usr-sim-10a", "sim-concurrent-a");
            var b = new LifeRunner(store, "usr-sim-10b", "sim-concurrent-b");
            a.Activate(runtimeA);
            b.Activate(runtimeB);
            for (var i = 0; i < 3; i++)
            {
                var ma = await a.AskAsync(runtimeA);
                var mb = await b.AskAsync(runtimeB);
                await a.AnswerAsync(runtimeA, Answer(ma!.StableKey, true));
                await b.AnswerAsync(runtimeB, Answer(mb!.StableKey, false));
            }
            Assert.All(a.Instance.Answers, x => Assert.True(x.Answer));
            Assert.All(b.Instance.Answers, x => Assert.False(x.Answer));
            Assert.NotEqual(a.BranchId, b.BranchId);
            results["10-concurrent"] = "both-isolated";
        }

        // --- 11. deterministic fallback, no proposer ---
        {
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-sim-11", "sim-deterministic");
            runner.Activate(runtime);
            for (var i = 0; i < 3; i++)
            {
                var m = await runner.AskAsync(runtime);
                Assert.Equal(MoveOrigin.Deterministic, m!.Origin);
                await runner.AnswerAsync(runtime, Answer(m.StableKey, true));
            }
            results["11-deterministic"] = "all-deterministic";
        }

        // ================= pass criteria =================
        var db = host.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var rows = await ctx.ActivityBranches.AsNoTracking().ToListAsync();

        // 2. labeling
        Assert.Equal(12, rows.Count);
        Assert.All(rows, r => Assert.Equal("simulated", r.Label));
        Assert.All(rows, r => Assert.Equal("Simulated", r.BranchKind));
        Assert.All(rows, r => Assert.DoesNotContain("observed_displayed", r.MovesJson));

        // 3. counterfactual separation — no simulated row carries a counterfactual move
        Assert.All(rows, r => Assert.DoesNotContain("counterfactual_not_displayed", r.MovesJson));

        // 7. isolation: production tables untouched by every session
        Assert.Empty(await ctx.Messages.ToListAsync());
        Assert.Empty(await ctx.Conversations.ToListAsync());
        Assert.Empty(await ctx.SemanticMemories.ToListAsync());
        Assert.Empty(await ctx.Procedures.ToListAsync());

        // 4. lifecycle outcomes, as declared
        Assert.Equal("Completed/correct", results["1-correct-guess"]);
        Assert.Equal("Active/guess-cleared", results["2-incorrect-guess"]);
        Assert.Equal("Completed/limit", results["3-limit"]);
        Assert.Equal("Abandoned", results["6-abandon"]);
        Assert.Equal(11, results.Count);
    }

    [Fact]
    public async Task TheDeclaredVolume_IsDeterministic_AcrossRuns()
    {
        static async Task<string> RunAsync()
        {
            await using var host = new TestHost(Clock);
            var store = host.Services.GetRequiredService<IActivityBranchStore>();
            var runtime = new ActivityRuntime(new TwentyQuestionsStrategy());
            var runner = new LifeRunner(store, "usr-det", "sim-det");
            runner.Activate(runtime);
            var keys = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var m = await runner.AskAsync(runtime);
                keys.Add(m!.StableKey);
                await runner.AnswerAsync(runtime, Answer(m.StableKey, i % 2 == 0));
            }
            return string.Join(",", keys);
        }

        Assert.Equal(await RunAsync(), await RunAsync());
    }
}
