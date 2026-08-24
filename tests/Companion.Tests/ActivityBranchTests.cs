using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Activities;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Source 1b: the counterfactual-shadow resolution, the persistent shadow store, and
/// activation resolution. Everything synthetic — no real conversation data.
/// </summary>
public class ActivityBranchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static ActivityInstance Instance(string id = "tq-01") => new()
    {
        InstanceId = id,
        ActivityType = "twenty-questions",
        StrategyVersion = "1",
        Lifecycle = ActivityLifecycle.Active,
        UserId = "usr-synth",
        ConversationId = Guid.Parse("33333333-0000-0000-0000-000000000003"),
        AskerParticipantId = "companion-ava",
        AnswererParticipantId = "usr-synth",
        QuestionLimit = 20,
        CurrentQuestionNumber = 4,
        ActivatedAt = T0,
        ActivationEvidence = "message:abc \"let's play 20 questions\"",
    };

    private static BranchMove Move(string key, MoveDisposition disposition, string? renderer = null)
        => new()
        {
            BranchId = "b1", MoveId = $"m-{key}",
            Move = new ActivityMove(ActivityMoveKind.Question, key, $"is it {key}"),
            Disposition = disposition, DisplayedRenderer = renderer,
            DisplayedQuestionId = disposition == MoveDisposition.CounterfactualNotDisplayed ? null : key,
            At = T0,
        };

    // ---- the counterfactual rule ----------------------------------------------------------

    [Fact]
    public void ACounterfactualBranch_NeverConsumesARealAnswer()
    {
        var branch = new ActivityBranch
        {
            BranchId = "b-cf", Kind = BranchKind.CounterfactualNative,
            ParentBranchId = "b-obs", BranchPointQuestionNumber = 4,
            Instance = Instance(),
            Moves = [Move("material-primary", MoveDisposition.CounterfactualNotDisplayed)],
        };

        var input = new ActivityInput(ActivityInputKind.Answer, "material-primary", true, "Yes",
            Guid.NewGuid(), T0);
        var decision = BranchBinding.CanBind(branch, input);

        Assert.False(decision.Allowed);
        Assert.Equal("counterfactual-branch-cannot-consume-user-input", decision.Reason);
        Assert.False(branch.CanAdvanceFromUserInput);
        Assert.False(branch.Moves[0].NextInputBindable);
        Assert.Equal("natural-counterfactual", branch.Label);
    }

    [Fact]
    public void ACounterfactualBranch_IsNeverAReportableNaturalSession()
    {
        var completed = new ActivityBranch
        {
            BranchId = "b-cf", Kind = BranchKind.CounterfactualNative,
            Instance = Instance() with { Lifecycle = ActivityLifecycle.Completed },
        };
        Assert.False(completed.IsReportableNaturalSession);

        var observed = completed with { Kind = BranchKind.ProductionObserved };
        Assert.True(observed.IsReportableNaturalSession);
    }

    [Fact]
    public void AnObservedBranch_BindsOnlyToTheQuestionActuallyDisplayed()
    {
        var branch = new ActivityBranch
        {
            BranchId = "b-obs", Kind = BranchKind.ProductionObserved,
            Instance = Instance(),
            Moves = [Move("indoors", MoveDisposition.ObservedDisplayed, renderer: "production-stheno")],
        };

        Assert.True(BranchBinding.CanBind(branch,
            new ActivityInput(ActivityInputKind.Answer, "indoors", true, "Yes", Guid.NewGuid(), T0)).Allowed);

        // An input naming a DIFFERENT question is refused — this is answer misassociation,
        // the second of the four December failures, caught at the boundary.
        var mismatch = BranchBinding.CanBind(branch,
            new ActivityInput(ActivityInputKind.Answer, "texture", true, "Yes", Guid.NewGuid(), T0));
        Assert.False(mismatch.Allowed);
        Assert.Equal("input-names-a-different-question", mismatch.Reason);
    }

    [Fact]
    public void ASimulatedBranch_MayBind_BecauseItsMovesWereDisplayedToTheSimulatedUser()
    {
        var branch = new ActivityBranch
        {
            BranchId = "b-sim", Kind = BranchKind.Simulated,
            Instance = Instance(),
            Moves = [Move("hand-held", MoveDisposition.SimulatedDisplayed, renderer: "native-simulated")],
        };

        Assert.True(BranchBinding.CanBind(branch,
            new ActivityInput(ActivityInputKind.Answer, "hand-held", true, "Yes", Guid.NewGuid(), T0)).Allowed);
        Assert.Equal("simulated", branch.Label);
        Assert.False(branch.IsReportableNaturalSession);   // simulated is never natural
    }

    [Fact]
    public void ABranchWithNoMove_HasNothingToAnswer()
    {
        var branch = new ActivityBranch
        {
            BranchId = "b-obs", Kind = BranchKind.ProductionObserved, Instance = Instance(),
        };
        Assert.Equal("no-move-awaiting-an-answer",
            BranchBinding.CanBind(branch, new ActivityInput(
                ActivityInputKind.Answer, "x", true, "Yes", Guid.NewGuid(), T0)).Reason);
    }

    // ---- the persistent shadow store -------------------------------------------------------

    private static ActivityBranchRecord Record(
        string branchId = "b-1", string retention = "no_training", string lifecycle = "Active")
        => new()
        {
            UserId = "usr-synth",
            ConversationId = Guid.Parse("33333333-0000-0000-0000-000000000003"),
            InstanceId = "tq-01", BranchId = branchId,
            BranchKind = "Simulated", Label = "simulated",
            ProcedureDefinitionId = Guid.Parse("44444444-0000-0000-0000-000000000004"),
            ActivityType = "twenty-questions", StrategyVersion = "1",
            Lifecycle = lifecycle, Version = 1,
            QuestionLimit = 20, CurrentQuestionNumber = 4,
            MovesJson = JsonSerializer.Serialize(new[] { new { key = "hand-held", text = "is it hand-held" } }),
            HypothesesJson = JsonSerializer.Serialize(new[] { "a synthetic personal item" }),
            ActivationEvidence = "message:abc synthetic activation",
            Retention = retention,
            ActivatedAt = T0, UpdatedAt = T0,
        };

    private static async Task<IActivityBranchStore> StoreAsync(TestHost host)
        => host.Services.GetRequiredService<IActivityBranchStore>();

    [Fact]
    public async Task TheStore_IsIdempotent_AndUsesOptimisticConcurrency()
    {
        await using var host = new TestHost(T0);
        var store = await StoreAsync(host);

        var first = await store.UpsertAsync(Record(), "input-1");
        Assert.True(first.Applied);
        Assert.Equal(1, first.Record.Version);

        // Duplicate delivery returns the EXISTING transition rather than applying twice.
        var duplicate = await store.UpsertAsync(Record(), "input-1");
        Assert.True(duplicate.Duplicate);
        Assert.False(duplicate.Applied);
        Assert.Equal(1, duplicate.Record.Version);

        // A stale version loses instead of overwriting.
        var stale = Record();
        stale.Version = 99;
        var conflict = await store.UpsertAsync(stale, "input-2");
        Assert.False(conflict.Applied);
        Assert.NotNull(conflict.Conflict);
    }

    [Fact]
    public async Task VolatileBranches_PersistMetadataButNotContent_AndSaySo()
    {
        await using var host = new TestHost(T0);
        var store = await StoreAsync(host);

        var result = await store.UpsertAsync(Record("b-vol", retention: "volatile_turn_only"), "k1");

        Assert.True(result.Applied);
        Assert.True(result.Record.ContentWithheld);   // restart-resume diagnosed unavailable
        Assert.Equal("[]", result.Record.MovesJson);
        Assert.Null(result.Record.HypothesesJson);
        Assert.Null(result.Record.ActivationEvidence);
        // Metadata survives so diagnostics still work.
        Assert.Equal("twenty-questions", result.Record.ActivityType);
        Assert.Equal(4, result.Record.CurrentQuestionNumber);
    }

    [Fact]
    public async Task SexualSubjectMatter_IsNotSuppressed_OnlyClassificationDecides()
    {
        await using var host = new TestHost(T0);
        var store = await StoreAsync(host);

        var record = Record("b-adult");
        record.HypothesesJson = JsonSerializer.Serialize(new[] { "a dildo" });
        record.FinalGuess = "is it a dildo";

        var stored = await store.UpsertAsync(record, "k1");

        // Ordinary retention: the content persists. Subject matter changed nothing.
        Assert.False(stored.Record.ContentWithheld);
        Assert.Contains("a dildo", stored.Record.HypothesesJson!);
        Assert.Equal("is it a dildo", stored.Record.FinalGuess);
    }

    [Fact]
    public async Task CleanupRemovesTerminalAndVolatileBranches_AndForgetSweepsByExcerpt()
    {
        await using var host = new TestHost(T0);
        var store = await StoreAsync(host);

        var terminal = Record("b-done", lifecycle: "Completed");
        terminal.TerminalAt = T0.AddDays(-40);
        terminal.UpdatedAt = T0.AddDays(-40);
        await store.UpsertAsync(terminal, "k1");
        await store.UpsertAsync(Record("b-live"), "k2");

        var cleaned = await store.CleanupAsync(T0, TimeSpan.FromDays(30), TimeSpan.FromDays(1));
        Assert.Equal(1, cleaned);
        Assert.Null(await store.GetAsync("b-done"));
        Assert.NotNull(await store.GetAsync("b-live"));

        var forgotten = await store.ForgetAsync(["a synthetic personal item"]);
        Assert.Equal(1, forgotten);
        Assert.Null(await store.GetAsync("b-live"));
    }

    [Fact]
    public async Task BranchesAreIsolatedPerUserAndConversation()
    {
        await using var host = new TestHost(T0);
        var store = await StoreAsync(host);

        var mine = Record("b-mine");
        var theirs = Record("b-theirs");
        theirs.UserId = "usr-other";
        await store.UpsertAsync(mine, "k1");
        await store.UpsertAsync(theirs, "k2");

        var found = await store.GetForConversationAsync("usr-synth", mine.ConversationId);
        Assert.Single(found);
        Assert.Equal("b-mine", found[0].BranchId);
    }

    // ---- activation resolution --------------------------------------------------------------

    private sealed class FakeProcedures(params Procedure[] procedures) : IProcedureStore
    {
        public Task<Procedure?> AddOrUpdateFromTeachingAsync(string u, Guid c, Message m, DateTimeOffset n, CancellationToken ct = default)
            => Task.FromResult<Procedure?>(null);
        public Task<Procedure?> ApplyRevisionAsync(string u, Message m, DateTimeOffset n, CancellationToken ct = default)
            => Task.FromResult<Procedure?>(null);
        public Task<IReadOnlyList<Procedure>> SearchAsync(string u, string q, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Procedure>>(procedures);
        public Task<IReadOnlyList<ProcedureRevision>> GetRevisionsAsync(string u, Guid id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProcedureRevision>>([]);
    }

    private static Procedure Proc(string name) => new()
    {
        Id = Guid.NewGuid(), UserId = "usr-synth", Name = name,
        Description = "synthetic", Status = ProcedureStatus.Active,
    };

    [Fact]
    public async Task Activation_RequiresAnExplicitRequestAndOneResolvedProcedure()
    {
        var resolver = new ActivityActivationResolver(new FakeProcedures(Proc("twenty questions")));

        var vague = await resolver.ResolveAsync("usr-synth",
            "I was thinking about games earlier", Guid.NewGuid(),
            "twenty-questions", "1", "companion-ava", "usr-synth", 20);
        Assert.False(vague.Activated);
        Assert.Equal("not-an-explicit-request", vague.Reason);

        var explicitRequest = await resolver.ResolveAsync("usr-synth",
            "Let's play 20 questions", Guid.NewGuid(),
            "twenty-questions", "1", "companion-ava", "usr-synth", 20);
        Assert.True(explicitRequest.Activated);
        Assert.NotNull(explicitRequest.ProcedureId);
        Assert.Contains("procedure:", explicitRequest.Evidence);
        Assert.Equal("companion-ava", explicitRequest.Definition!.AskerParticipantId);
    }

    [Fact]
    public async Task AmbiguousProcedures_ProduceClarification_NotASilentPick()
    {
        var resolver = new ActivityActivationResolver(
            new FakeProcedures(Proc("twenty questions"), Proc("twenty questions advanced")));

        var decision = await resolver.ResolveAsync("usr-synth", "let's play 20 questions",
            Guid.NewGuid(), "twenty-questions", "1", "companion-ava", "usr-synth", 20);

        Assert.False(decision.Activated);
        Assert.True(decision.NeedsClarification);
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public async Task NoMatchingProcedure_IsADiagnosedNonActivation()
    {
        var resolver = new ActivityActivationResolver(new FakeProcedures(Proc("sourdough starter")));
        var decision = await resolver.ResolveAsync("usr-synth", "let's play 20 questions",
            Guid.NewGuid(), "twenty-questions", "1", "companion-ava", "usr-synth", 20);

        Assert.False(decision.Activated);
        Assert.Equal("no-matching-procedure", decision.Reason);
        Assert.Contains("sourdough starter", decision.Candidates);
    }
}
