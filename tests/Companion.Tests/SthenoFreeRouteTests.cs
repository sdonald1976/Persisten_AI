using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.Execution;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The Stheno-free route (Companion:SthenoFree): the routed user's turn is anchored on the
/// native plan/4 and the mouth, and the conversational model is NEVER called - not for
/// generation, and not as any fallback. These tests pin that contract at the strongest seam
/// available: the reply generator used here throws on contact, so a single stray call fails
/// the test rather than merely being counted.
/// </summary>
public class SthenoFreeRouteTests
{
    private const string RouteUser = "demo-user";

    // ---- fixtures ------------------------------------------------------------------------

    /// <summary>The route's proof by construction: any call to the generator is a test failure.</summary>
    private sealed class ThrowingReplyGenerator : IReplyGenerator
    {
        public int Calls;

        public Task<ChatCompletion> GenerateAsync(
            string systemPrompt, string userMessage, IProgress<string>? sink = null,
            string? speaker = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException(
                "The conversational model was invoked on the Stheno-free route.");
        }
    }

    /// <summary>A mouth with a scripted verdict, so every fallback class can be forced.</summary>
    private sealed class ScriptedMouth : IRendererShadow
    {
        public RendererCanaryResult? Next { get; set; }
        public RendererShadowObservation? SeenObservation;
        public int MouthRenders;

        public bool IsObserving => false;
        public bool IsMouthObserving => false;
        public MouthCounters MouthCounters => new(0, 0, 0, 0, null);
        public RendererShadowCounters Counters => new(0, 0, 0, 0, 0);
        public bool IsCanaryFor(string userId) => false;
        public bool IsMouthCanaryFor(string userId) => false;
        public void Observe(RendererShadowObservation observation) { }
        public void ObservePlanOnly(RendererShadowObservation observation) { }
        public void ObserveMouth(RendererShadowObservation observation) { }
        public Task<(bool Ok, string Detail)> VerifyMouthIdentityAsync(CancellationToken ct)
            => Task.FromResult((true, "scripted"));

        public Task<RendererCanaryResult?> RenderForDisplayAsync(
            RendererShadowObservation obs, bool record, CancellationToken ct)
            => Task.FromResult<RendererCanaryResult?>(null);

        public Task<RendererCanaryResult?> RenderMouthForDisplayAsync(
            RendererShadowObservation obs, bool record, CancellationToken ct)
        {
            MouthRenders++;
            SeenObservation = obs;
            return Task.FromResult(Next);
        }
    }

    private sealed class SilentDiagnostics : IDiagnosticsStore
    {
        public Task RecordModelCallAsync(ModelCallRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallRecord>> GetRecentToolCallsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolCallRecord>>([]);
        public Task<IReadOnlyList<ModelRoleStats>> GetModelStatsAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelRoleStats>>([]);
        public Task RecordTurnAsync(TurnRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TurnRecord>> GetRecentTurnsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TurnRecord>>([]);
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetByEvidenceAsync(string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static TurnExecution Execution(
        ThrowingReplyGenerator generator, ScriptedMouth mouth)
    {
        var toolLoop = new Companion.Core.Services.ToolLoop(
            [], new QueuedChatModel("""{"tool": null}"""), new SilentDiagnostics(),
            Options.Create(new CompanionOptions()), TimeProvider.System,
            NullLogger<Companion.Core.Services.ToolLoop>.Instance);

        return new TurnExecution(
            toolLoop, generator, mouth, NullLogger<TurnExecution>.Instance,
            options: Options.Create(new CompanionOptions
            {
                SthenoFree = new SthenoFreeOptions { Enabled = true, UserId = RouteUser },
            }));
    }

    private static PlanV3.PlanV3 NativePlan(
        QuestionPolicy question = QuestionPolicy.question_forbidden,
        params PlanItem[] items)
        => new()
        {
            TraceId = Guid.NewGuid(),
            Participants =
            [
                new Participant(RouteUser, ParticipantRole.user, "Scott"),
                new Participant("companion-ava", ParticipantRole.companion, "Ava"),
            ],
            Act = "acknowledge",
            Question = new QuestionPolicyBlock(question),
            Items = items,
        };

    private static PlanItem Item(string id, ExpressionPolicy policy, string text)
        => new()
        {
            Id = id, Type = "note", Policy = policy, Text = text, Source = "retrieval",
            ReasonCode = policy == ExpressionPolicy.must_not_express
                ? "epistemic-integrity.superseded-or-disputed" : null,
        };

    private static TurnExecutionRequest Request(
        string userId = RouteUser,
        PlanV3.PlanV3? native = null,
        bool inCharacter = false,
        Companion.Core.Services.ToolLoop.Outcome? tools = null)
        => new()
        {
            TraceId = Guid.NewGuid(),
            UserId = userId,
            ConversationId = Guid.NewGuid(),
            SourceMessageId = Guid.NewGuid(),
            PromptText = "is the bike alright?",
            Packet = new ContextPacket { UserMessage = "is the bike alright?" },
            Recent = [],
            Plan = new ResponsePlan
            {
                Act = TurnIntent.Acknowledge,
                Question = null,
                Content = [],
                Epistemic = [],
                Tone = new ToneGuidance("short and casual", null, null),
            },
            ToolOutcome = tools ?? new([], [], null, [], 0),
            InCharacter = inCharacter,
            Sensitive = false,
            NativeV3 = native,
        };

    private static PlanV3.PlanV3 TyrePlan() => NativePlan(
        QuestionPolicy.question_forbidden,
        Item("f1", ExpressionPolicy.must_express, "the back tyre is flat again"),
        Item("unk1", ExpressionPolicy.admit_unknown, "whether it is the same puncture as before"),
        Item("sup1", ExpressionPolicy.must_not_express, "the meeting is on Thursday"));

    // ---- the route ------------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulMouthTurn_DisplaysTheMouthReply_AndNeverTouchesTheGenerator()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth
        {
            Next = new RendererCanaryResult(
                "The back tyre is flat again - I don't know if it's the same puncture.",
                [], 1200, CriticalFailure: false),
        };

        var result = await Execution(generator, mouth).ExecuteAsync(Request(native: TyrePlan()));

        Assert.Equal(0, generator.Calls);
        Assert.Equal("run-2.1", result.SelectedRenderer);
        Assert.Equal(mouth.Next!.Reply, result.Displayed);
        Assert.Null(result.FallbackReason);
        // The mouth observation's fallback text stands in for "production": it must be the
        // deterministic rendering, never anything a conversational model wrote.
        Assert.Equal(DeterministicMouth.Render(TyrePlan()), mouth.SeenObservation!.ProductionResponse);
    }

    [Fact]
    public async Task MouthUnavailable_FallsBackToTheDeterministicRender_NeverTheGenerator()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth { Next = null };

        var result = await Execution(generator, mouth).ExecuteAsync(Request(native: TyrePlan()));

        Assert.Equal(0, generator.Calls);
        Assert.Equal("plan-deterministic", result.SelectedRenderer);
        Assert.Equal(DeterministicMouth.Render(TyrePlan()), result.Displayed);
        Assert.Contains("unavailable", result.FallbackReason);
    }

    [Fact]
    public async Task CriticalGuardFailure_FallsBackToTheDeterministicRender_WithTheTypedReason()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth
        {
            Next = new RendererCanaryResult(
                "Zydeco is a lively genre.", ["epistemic-admission-absent: no admission"],
                900, CriticalFailure: true),
        };

        var result = await Execution(generator, mouth).ExecuteAsync(Request(native: TyrePlan()));

        Assert.Equal(0, generator.Calls);
        Assert.Equal("plan-deterministic", result.SelectedRenderer);
        Assert.Contains("epistemic-admission-absent", result.FallbackReason);
        Assert.Equal(DeterministicMouth.Render(TyrePlan()), result.Displayed);
    }

    [Fact]
    public async Task NoNativePlan_ProducesTheTypedHonestFailure_NeverTheGenerator()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth();

        var result = await Execution(generator, mouth).ExecuteAsync(Request(native: null));

        Assert.Equal(0, generator.Calls);
        Assert.Equal(0, mouth.MouthRenders);
        Assert.Equal("honest-failure", result.SelectedRenderer);
        Assert.Equal(DeterministicMouth.HonestFailure, result.Displayed);
    }

    [Fact]
    public async Task ToolTurn_StillRoutesThroughTheMouth()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth
        {
            Next = new RendererCanaryResult("Found it in the shed notes.", [], 800, false),
        };
        var tools = new Companion.Core.Services.ToolLoop.Outcome(
            ["memory.search"],
            [new ToolCallTrace { Tool = "memory.search", Ok = true, Code = "ok" }],
            "results", [], 1);

        var result = await Execution(generator, mouth)
            .ExecuteAsync(Request(native: TyrePlan(), tools: tools));

        Assert.Equal(0, generator.Calls);
        Assert.Equal(1, mouth.MouthRenders);
        Assert.Equal("run-2.1", result.SelectedRenderer);
    }

    [Fact]
    public async Task InCharacterTurn_SkipsTheMouth_AndRendersDeterministically()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth();

        var result = await Execution(generator, mouth)
            .ExecuteAsync(Request(native: TyrePlan(), inCharacter: true));

        Assert.Equal(0, generator.Calls);
        Assert.Equal(0, mouth.MouthRenders);
        Assert.Equal("plan-deterministic", result.SelectedRenderer);
        Assert.Contains("in-character", result.FallbackReason);
    }

    [Fact]
    public async Task AUserOffTheRoute_TakesTheProductionPath()
    {
        var generator = new ThrowingReplyGenerator();
        var mouth = new ScriptedMouth();

        // The generator throwing here is the assertion: the off-route user MUST reach it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Execution(generator, mouth).ExecuteAsync(
                Request(userId: "someone-else", native: TyrePlan())));
        Assert.Equal(1, generator.Calls);
    }

    // ---- the deterministic floor ----------------------------------------------------------

    [Fact]
    public void DeterministicRender_StatesMusts_AdmitsUnknowns_AndSuppressesNever()
    {
        var text = DeterministicMouth.Render(TyrePlan());

        Assert.Contains("back tyre is flat again", text);
        // The admission must satisfy the SAME marker family the ADMIT guard checks, or the
        // fallback for a failed admission would itself fail the admission guard.
        Assert.True(Companion.Core.Validation.UncertaintyMarkers.AdmitsNotLearned(text));
        Assert.DoesNotContain("Thursday", text);
        Assert.DoesNotContain("?", text);
    }

    [Fact]
    public void DeterministicRender_AsksExactlyWhenRequired()
    {
        var ask = NativePlan(
            QuestionPolicy.ask_required,
            Item("f1", ExpressionPolicy.must_express, "the plumber can come Thursday"),
            Item("q1", ExpressionPolicy.ask_required, "does Thursday morning work for you"));
        var askPlan = ask with { Question = new QuestionPolicyBlock(QuestionPolicy.ask_required, "q1") };

        var text = DeterministicMouth.Render(askPlan);
        Assert.EndsWith("?", text);

        var closed = DeterministicMouth.Render(TyrePlan());
        Assert.DoesNotContain("?", closed);
    }

    [Fact]
    public void DeterministicRender_OfAnEmptyPlan_IsTheHonestFailure()
    {
        Assert.Equal(DeterministicMouth.HonestFailure,
            DeterministicMouth.Render(NativePlan(QuestionPolicy.question_forbidden)));
    }
}
