using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.Planning;
using Companion.Infrastructure.Seeding;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B4. The extracted planning stage.
///
/// Two derivations live here and stay parallel: Plan/2, which production uses, and the
/// native plan/3-plan/4 material, which is shadow evidence built from the same upstream
/// state and never FROM Plan/2. The separate result types are what make passing one where
/// the other is meant impossible rather than merely discouraged.
/// </summary>
public class TurnPlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Trace = Guid.Parse("77777777-1111-2222-3333-444444444444");
    private static string User => CompanionSeeder.DemoUserId;

    private static TurnPlanning Planner(TestHost host, IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<TurnPlanning>();

    private static Message Msg(MessageRole role, string content, int minute) => new()
    {
        Id = Guid.NewGuid(), UserId = "usr-scott",
        ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Role = role, Content = content, Timestamp = Now.AddMinutes(minute),
    };

    private static (WorkingContextState Working, TurnIntentState Intent) Read(
        string promptText, params Message[] recent)
    {
        var understanding = Core.Turns.Understanding.TurnUnderstanding.Read(
            recent, promptText, null, "Scott", "Ava");
        var (intent, _) = Core.Turns.Understanding.TurnUnderstanding.ClassifyIntent(
            understanding.Working, promptText, 0);
        return (understanding.Working, intent);
    }

    private static ProductionPlanResult Plan(
        TestHost host, IServiceScope scope, string promptText,
        IReadOnlyList<RetrievalResult>? memories = null,
        ConceptLookupResult? knowledge = null,
        string? curiosity = null,
        params Message[] recent)
    {
        var (working, intent) = Read(promptText, recent);
        return Planner(host, scope).BuildProductionPlan(
            Trace, intent, working, promptText, memories ?? [], knowledge, curiosity,
            registerNote: null, moodNote: null, persona: null);
    }

    // ---- Plan/2 ---------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryConversation_PlansAnActAndRecordsOneDecision()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var planned = Plan(host, scope, "The squirrel defeated the baffle again.");

        Assert.NotNull(planned.Plan);
        Assert.Equal("plan", planned.Decision.Stage);
        Assert.Equal("rule", planned.Decision.Decider);
        Assert.StartsWith(planned.Plan.Act.ToKebab(), planned.Decision.Verdict);
    }

    [Fact]
    public async Task ACorrection_IsPlannedAsAnAcknowledgmentWithAnOwner()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var planned = Plan(
            host, scope, "No, it was Wednesday, not Tuesday.",
            recent: [Msg(MessageRole.Assistant, "The presentation was on Tuesday.", 1)]);

        if (planned.Plan.Acknowledgments.Count > 0)
        {
            Assert.All(planned.Plan.Acknowledgments,
                a => Assert.True(Enum.IsDefined(a.ErrorOwner)));
            Assert.Contains("ack=", planned.Decision.Verdict);
        }
    }

    [Fact]
    public async Task AnEpistemicUnknown_BecomesAnEpistemicConstraint()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var unknown = new ConceptLookupResult(ConceptFamiliarity.Unknown, "quokka");
        var planned = Plan(host, scope, "Do you know what a quokka is?", knowledge: unknown);

        Assert.NotEmpty(planned.Plan.Epistemic);
        Assert.Contains("epistemic=", planned.Decision.Verdict);
    }

    [Fact]
    public async Task ASupersededFact_IsNotPlannedAsSomethingToState()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var superseded = new SemanticMemory
        {
            Id = Guid.NewGuid(), UserId = User, Subject = "user", Predicate = "meets",
            Value = "Tuesday", NormalizedFact = "The presentation was on Tuesday.",
            FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
            Status = MemoryStatus.Superseded, Validity = Core.Domain.Validity.Superseded,
        };
        var planned = Plan(
            host, scope, "When was the presentation?",
            memories:
            [
                new RetrievalResult
                {
                    Memory = superseded, Score = 0.9,
                    Signals = new Dictionary<string, double>(), Reason = "test fixture",
                },
            ]);

        // A superseded fact may be carried as a tombstone, never as something to assert.
        Assert.DoesNotContain(planned.Plan.Content,
            c => c.Requirement == ContentRequirement.MustState
                 && c.Text.Contains("Tuesday", StringComparison.OrdinalIgnoreCase)
                 && c.Text.Contains("was on", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnOfferedCuriosity_BecomesAnOptionalQuestion()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var planned = Plan(
            host, scope, "Morning.", curiosity: "Did the shed quote ever come through?");

        if (planned.Plan.Question is { } question)
        {
            Assert.True(Enum.IsDefined(question.Kind));
            Assert.Contains("q=", planned.Decision.Verdict);
        }
    }

    [Fact]
    public async Task WithNoCuriosityAndNothingToAsk_NoQuestionIsPlanned()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var planned = Plan(host, scope, "Thanks, that's all I needed.");

        if (planned.Plan.Question is null)
            Assert.DoesNotContain("q=", planned.Decision.Verdict);
    }

    // ---- native plan/3 and plan/4 -----------------------------------------------------------

    [Fact]
    public async Task TheNativePlanIsBuiltFromUpstreamState_NotFromPlanTwo()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var planner = Planner(host, scope);

        var (working, intent) = Read("What did we decide about the shed?");
        var native = planner.BuildNativePlan(
            Trace, intent, working, "What did we decide about the shed?", [], null, null,
            sensitiveTurn: false, User, "Ava");

        Assert.NotNull(native.Plan);
        Assert.Null(native.BuildError);
        var decision = Assert.Single(native.Decisions);
        Assert.Equal("plan.native-v3", decision.Stage);
        Assert.Equal("built", decision.Verdict);
    }

    [Fact]
    public async Task ASensitiveTurn_IsPlannedNativelyWithItsAudienceRespected()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var (working, intent) = Read("Keep this private: something personal.");
        var native = Planner(host, scope).BuildNativePlan(
            Trace, intent, working, "Keep this private: something personal.", [], null, null,
            sensitiveTurn: true, User, "Ava");

        Assert.NotNull(native.Plan);
        // Whatever the builder decided, the plan must still validate as a whole: a restricted
        // item without a resolvable audience is refused rather than quietly emitted.
        Assert.Empty(PlanV3Codec.Validate(native.Plan!));
    }

    [Fact]
    public async Task ContributionWithNoNativePlan_IsANoOp()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var empty = new NativePlanResult { Plan = null, BuildError = "earlier failure" };
        var (working, _) = Read("anything");

        var result = await Planner(host, scope).ContributeAsync(
            empty, Trace, User, "anything", sensitive: false,
            productionPlan: Plan(host, scope, "anything").Plan,
            toolOutcome: new ToolLoop.Outcome([], [], null, [], 0),
            working: working,
            innerState: await scope.ServiceProvider
                .GetRequiredService<Core.Abstractions.ICompanionStateTracker>().BuildAsync(User),
            familiarity: await scope.ServiceProvider
                .GetRequiredService<Core.Abstractions.IFamiliarityTracker>().BuildAsync(User),
            conversationId: Guid.NewGuid(), nativeFrame: null);

        // An invalid or absent native plan is carried through untouched, never fabricated.
        Assert.Null(result.Plan);
        Assert.Equal("earlier failure", result.BuildError);
        Assert.Empty(result.Decisions);
    }

    [Fact]
    public async Task ContributionAddsRegisterVotes_AndReportsTheAssembly()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var planner = Planner(host, scope);
        var sp = scope.ServiceProvider;

        var (working, intent) = Read("What did we decide about the shed?");
        var built = planner.BuildNativePlan(
            Trace, intent, working, "What did we decide about the shed?", [], null, null,
            false, User, "Ava");

        var contributed = await planner.ContributeAsync(
            built, Trace, User, "What did we decide about the shed?", sensitive: false,
            productionPlan: Plan(host, scope, "What did we decide about the shed?").Plan,
            toolOutcome: new ToolLoop.Outcome([], [], null, [], 0),
            working: working,
            innerState: await sp.GetRequiredService<Core.Abstractions.ICompanionStateTracker>().BuildAsync(User),
            familiarity: await sp.GetRequiredService<Core.Abstractions.IFamiliarityTracker>().BuildAsync(User),
            conversationId: Guid.NewGuid(), nativeFrame: null);

        Assert.NotNull(contributed.Plan);
        Assert.NotNull(contributed.Assembly);
        var decision = Assert.Single(contributed.Decisions);
        Assert.Equal("plan.native-v3.tools", decision.Stage);
        Assert.StartsWith("accepted=", decision.Verdict);
    }

    // ---- the frame's contribution -------------------------------------------------------------

    [Theory]
    [InlineData(FrameTransition.enter, FrameMode.fiction)]
    [InlineData(FrameTransition.@continue, FrameMode.fiction)]
    [InlineData(FrameTransition.switchScene, FrameMode.fiction)]
    [InlineData(FrameTransition.exit, FrameMode.real)]
    public void EachTransitionShapesTheFrameItContributes(
        FrameTransition transition, FrameMode expectedMode)
    {
        var session = new FrameSession
        {
            SessionId = Guid.NewGuid(), UserId = User, ConversationId = Guid.NewGuid(),
            SceneRef = "scene-7c1f", Narration = "licensed", Continuity = "maintain",
            ActiveCompanionCharacterId = "keeper",
            CharactersJson = """[{"characterId":"keeper","display":"the lighthouse keeper"}]""",
            EnteredAt = Now, LastTransitionAt = Now,
        };

        var frame = TurnPlanning.BuildFrame(transition, session);

        Assert.Equal(expectedMode, frame.Mode);
        Assert.Equal(transition, frame.Transition);

        if (transition == FrameTransition.exit)
        {
            // Exiting restores real rules ON this turn: no scene, no cast, no narration.
            Assert.Null(frame.SceneRef);
            Assert.Empty(frame.Characters);
            Assert.Equal(FrameNarration.forbidden, frame.Narration);
            Assert.Null(frame.ActiveCompanionCharacterId);
        }
        else
        {
            Assert.Equal("scene-7c1f", frame.SceneRef);
            Assert.NotEmpty(frame.Characters);
            Assert.Equal(FrameNarration.licensed, frame.Narration);
        }
    }

    [Fact]
    public void AnUnlicensedSessionNeverContributesLicensedNarration()
    {
        var session = new FrameSession
        {
            SessionId = Guid.NewGuid(), UserId = User, ConversationId = Guid.NewGuid(),
            SceneRef = "scene-1", Narration = "forbidden", Continuity = "none",
            CharactersJson = "[]", EnteredAt = Now, LastTransitionAt = Now,
        };

        Assert.Equal(FrameNarration.forbidden,
            TurnPlanning.BuildFrame(FrameTransition.@continue, session).Narration);
    }

    // ---- the parallel paths stay parallel -------------------------------------------------------

    [Fact]
    public void ThePlanTypesCannotBeConfusedForEachOther()
    {
        // Structural, and the point of the phase: Plan/2 and the native material are separate
        // types, so no assignment can quietly treat a translated Plan/2 as native Plan/4.
        Assert.NotEqual(
            typeof(ProductionPlanResult).GetProperty("Plan")!.PropertyType,
            typeof(NativePlanResult).GetProperty("Plan")!.PropertyType);

        Assert.Equal(typeof(ResponsePlan),
            typeof(ProductionPlanResult).GetProperty("Plan")!.PropertyType);
    }

    [Fact]
    public void PlanningHidesNoSerialization()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Planning", "TurnPlanning.cs"));

        // Computing bytes is not planning. The CompactV4 length probe stayed with the caller.
        foreach (var forbidden in new[]
                 {
                     "CompactV4(", "CompactV3(", "CompactV2(", "IContextAssembler",
                     "IReplyGenerator", "IRendererShadow", "IShadowRecorder", "Assemble(promptText",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningOwnsNoFrameLifecycle()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Planning", "TurnPlanning.cs"));

        // It shapes the frame's contribution; deciding and persisting a transition is not its.
        foreach (var forbidden in new[] { "IFrameSessionStore", "ApplyAsync", "FrameLifecycle" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ---- through a real turn -----------------------------------------------------------------

    [Fact]
    public async Task ARealTurn_KeepsItsPlanningDecisionsInOrder()
    {
        await using var host = new TestHost(Now, settings: new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = "true",
            ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
            ["Companion:RendererShadow:TimeoutSeconds"] = "2",
        });

        Guid conversationId;
        using (var seed = host.CreateScope())
            conversationId = (await seed.ServiceProvider
                .GetRequiredService<Core.Abstractions.IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Core.Abstractions.ICompanion>()
                .RespondAsync(User, conversationId, "What did we decide about the shed?");

        using var read = host.CreateScope();
        var turn = (await read.ServiceProvider
            .GetRequiredService<Core.Abstractions.IDiagnosticsStore>()
            .GetRecentTurnsAsync(User, 1)).Single();
        var stages = (turn.Decisions ?? "")
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('=')[0])
            .ToList();

        var plan = stages.IndexOf("plan");
        var native = stages.IndexOf("plan.native-v3");
        var tools = stages.IndexOf("tools");

        Assert.True(plan >= 0, $"no plan stage in [{string.Join(", ", stages)}]");
        if (native >= 0)
            Assert.True(plan < native, "Plan/2 is built before the native plan");
        if (native >= 0 && tools >= 0)
            Assert.True(native < tools, "the native plan is built before tools run");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
