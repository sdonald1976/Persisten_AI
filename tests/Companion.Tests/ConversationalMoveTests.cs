using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.Execution;
using Companion.Infrastructure.Models;
using Companion.PlanV3;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The regression suite for the failed live acceptance test (2026-08-31): fabricated
/// greeting content, a mirrored user question, an accepted invitation re-issued verbatim
/// across two turns, and promised content never supplied. Each failure mode is pinned at the
/// seam that actually produced it.
/// </summary>
public class ConversationalMoveTests
{
    private const string Invitation = "Ready for something a bit more adventurous?";

    // ---- move identity ---------------------------------------------------------------------

    [Fact]
    public void MoveIdentity_IsSemantic_NotLiteral()
    {
        // A rephrasing of the same move collides; a different move does not.
        Assert.Equal(MoveIdentity.Of(Invitation),
            MoveIdentity.Of("ready for something more adventurous"));
        Assert.NotEqual(MoveIdentity.Of(Invitation), MoveIdentity.Of("ready for lunch?"));
    }

    [Fact]
    public void TheStore_TracksPendingAndSatisfied()
    {
        var store = new InMemoryConversationMoveStore();
        var conv = Guid.NewGuid();
        var move = new PendingMove
        {
            Kind = PendingMoveKind.Invitation,
            Text = Invitation,
            Identity = MoveIdentity.Of(Invitation),
        };

        store.SetPending(conv, move);
        Assert.Equal(move, store.GetPending(conv));

        store.MarkSatisfied(conv, move.Identity);
        Assert.Null(store.GetPending(conv));                 // satisfied clears pending
        Assert.True(store.IsSatisfied(conv, move.Identity));
        Assert.Contains(move.Identity, store.SatisfiedIdentities(conv));

        // Isolation: another conversation shares nothing.
        Assert.False(store.IsSatisfied(Guid.NewGuid(), move.Identity));
    }

    // ---- the builder: an acknowledgment is context, not an utterance obligation -------------

    [Fact]
    public void AnswerReceivedAcknowledgment_IsBackgroundOnly()
    {
        // The live failure's mechanism: this item was must_express with the bound question as
        // its text, so the plan itself obliged the mouth to convey "Ready for something a bit
        // more adventurous?" - and on a thin plan that WAS the reply, twice.
        var built = PlanV3Builder.Build(
            Guid.NewGuid(),
            new TurnIntentState { Intent = TurnIntent.RespondToAnswer },
            new WorkingContextState
            {
                Move = ConversationMove.AnswersOpenQuestion,
                BoundQuestion = Invitation,
                ReferenceMarkers = [],
                RawQuery = "q",
                RetrievalQuery = "q",
            },
            "Absolutely!", [], null, null, sensitiveTurn: false,
            "demo-user", "Scott", "companion-ava", "Ava");

        var ack = Assert.Single(built.Plan!.Items, i => i.Type == "answer-received");
        Assert.Equal(ExpressionPolicy.background_only, ack.Policy);
    }

    // ---- the route: identity vetoes ---------------------------------------------------------

    private sealed class ThrowingReplyGenerator : IReplyGenerator
    {
        public Task<ChatCompletion> GenerateAsync(
            string systemPrompt, string userMessage, IProgress<string>? sink = null,
            string? speaker = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Stheno was invoked on the Stheno-free route.");
    }

    private sealed class ScriptedMouth(string reply) : IRendererShadow
    {
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
            => Task.FromResult<RendererCanaryResult?>(
                new RendererCanaryResult(reply, [], 900, CriticalFailure: false));
    }

    private static async Task<TurnExecutionResult> RouteTurnAsync(
        string userMessage, string mouthReply, IReadOnlyCollection<string> suppressed)
    {
        var toolLoop = new ToolLoop(
            [], new QueuedChatModel("""{"tool": null}"""), new NoDiagnostics(),
            Options.Create(new CompanionOptions()), TimeProvider.System,
            NullLogger<ToolLoop>.Instance);
        var execution = new TurnExecution(
            toolLoop, new ThrowingReplyGenerator(), new ScriptedMouth(mouthReply),
            NullLogger<TurnExecution>.Instance,
            options: Options.Create(new CompanionOptions
            {
                SthenoFree = new SthenoFreeOptions { Enabled = true, UserId = "demo-user" },
            }));

        return await execution.ExecuteAsync(new TurnExecutionRequest
        {
            TraceId = Guid.NewGuid(),
            UserId = "demo-user",
            ConversationId = Guid.NewGuid(),
            SourceMessageId = Guid.NewGuid(),
            PromptText = userMessage,
            Packet = new ContextPacket { UserMessage = userMessage },
            Recent = [],
            Plan = new ResponsePlan
            {
                Act = TurnIntent.Acknowledge,
                Question = null,
                Content = [],
                Epistemic = [],
                Tone = new ToneGuidance("short and casual", null, null),
            },
            ToolOutcome = new([], [], null, [], 0),
            InCharacter = false,
            Sensitive = false,
            NativeV3 = new PlanV3.PlanV3
            {
                TraceId = Guid.NewGuid(),
                Participants =
                [
                    new Participant("demo-user", ParticipantRole.user, "Scott"),
                    new Participant("companion-ava", ParticipantRole.companion, "Ava"),
                ],
                Act = "acknowledge",
                Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
                Items =
                [
                    new PlanItem
                    {
                        Id = "f1", Type = "note", Policy = ExpressionPolicy.must_express,
                        Text = "the build finished this morning", Source = "retrieval",
                    },
                ],
            },
            SuppressedMoveIdentities = suppressed,
        });
    }

    [Fact]
    public async Task AMouthReplyThatReissuesASatisfiedMove_FallsBack_NeverStheno()
    {
        // The transcript's turns 4 and 5: "Absolutely!" / "Yes, let's be adventurous" each got
        // the invitation back. With the invitation's identity marked satisfied, the same mouth
        // output now falls back to the deterministic plan rendering instead of displaying.
        var result = await RouteTurnAsync(
            "Absolutely!", Invitation, [MoveIdentity.Of(Invitation)]);

        Assert.Equal("plan-deterministic", result.SelectedRenderer);
        Assert.Contains("re-issued an already-satisfied move", result.FallbackReason);
        Assert.DoesNotContain("adventurous", result.Displayed);
    }

    [Fact]
    public async Task AMouthReplyThatEchoesTheUser_FallsBack_NeverStheno()
    {
        // The transcript's second turn: "How's improvements with Claude going?" answered by
        // itself. Identity comparison catches the echo regardless of punctuation drift.
        var result = await RouteTurnAsync(
            "How's improvements with Claude going?",
            "How's improvements with Claude going?", []);

        Assert.Equal("plan-deterministic", result.SelectedRenderer);
        Assert.Contains("echoed the user's message", result.FallbackReason);
    }

    [Fact]
    public async Task AnHonestDifferentReply_StillDisplays()
    {
        var result = await RouteTurnAsync(
            "How's improvements with Claude going?",
            "The build finished this morning, so it's coming along.", [MoveIdentity.Of(Invitation)]);

        Assert.Equal("run-2.1", result.SelectedRenderer);
    }

    // ---- the planner's authority layer ------------------------------------------------------

    private static PlanV3.PlanV3 BarePlan() => new()
    {
        TraceId = Guid.NewGuid(),
        Participants =
        [
            new Participant("demo-user", ParticipantRole.user, "Scott"),
            new Participant("companion-ava", ParticipantRole.companion, "Ava"),
        ],
        Act = "answer-question",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Items =
        [
            new PlanItem
            {
                Id = "f1", Type = "note", Policy = ExpressionPolicy.must_express,
                Text = "the project is going well", Source = "retrieval",
            },
        ],
    };

    private static LlmExecutivePlanner Planner(string reply)
        => new(new QueuedChatModel(reply), NullLogger<LlmExecutivePlanner>.Instance);

    [Fact]
    public async Task ACreativeProposal_IsRefused_WhenFictionWasNotInvited()
    {
        // "new techniques... a bit of spice" was fabricated content on a plain turn. A planner
        // proposing it as creative is refused by the authority layer, not by luck.
        var planner = Planner(
            """{"include":[],"order":[],"ask":false,"propose":[{"kind":"creative","text":"Some new twists and turns to add spice."}]}""");

        var outcome = await planner.RefineAsync(BarePlan(), new PlanningSignals
        {
            UserMessage = "What kind of new techniques are we talking about here?",
            CreativeInvited = false,
        });

        Assert.Contains("refused 1", outcome.Decision.Reason);
        Assert.DoesNotContain(outcome.Plan.Items, i => i.Type == "creative");
    }

    [Fact]
    public async Task AGroundedProposal_WithoutEvidence_IsRefused_WithEvidence_CarriesIt()
    {
        var memId = Guid.NewGuid();
        var citable = "mem:" + memId.ToString("N")[..8];
        var planner = Planner(
            $$"""{"include":[],"order":[],"ask":false,"propose":[{"kind":"grounded","text":"The shed reorganisation made things findable."},{"kind":"grounded","text":"The wrench turned up in the shed.","basedOn":["{{citable}}"]}]}""");

        var outcome = await planner.RefineAsync(BarePlan(), new PlanningSignals
        {
            UserMessage = "how did that go?",
            Memories = [(memId, "The user found a missing socket wrench in the garden shed.")],
        });

        var admitted = Assert.Single(outcome.Plan.Items, i => i.Provenance?.Origin == "executive-grounded");
        Assert.Equal(citable, admitted.Provenance!.EvidenceRef);
        Assert.Equal(ExpressionPolicy.may_express, admitted.Policy);
        Assert.Contains("refused 1", outcome.Decision.Reason);
    }

    [Fact]
    public async Task AnAutobiographicalInference_IsRefused()
    {
        var planner = Planner(
            """{"include":[],"order":[],"ask":false,"propose":[{"kind":"inference","text":"I've tried a few of those techniques myself and loved them."}]}""");

        var outcome = await planner.RefineAsync(BarePlan(), new PlanningSignals
        {
            UserMessage = "what techniques?",
        });

        Assert.Contains("refused 1", outcome.Decision.Reason);
        Assert.DoesNotContain(outcome.Plan.Items, i => i.Provenance?.Origin == "executive-inference");
    }

    [Fact]
    public async Task AnAdmitProposal_BecomesAnAdmitUnknownItem()
    {
        // Test 3's honest path: asked about progress with nothing behind it, the plan gains an
        // ADMIT obligation instead of a fabrication - and Run-2.1 was corrected precisely to
        // render those.
        var planner = Planner(
            """{"include":[],"order":[],"ask":false,"propose":[{"kind":"admit","text":"whether any new techniques have actually been explored"}]}""");

        var outcome = await planner.RefineAsync(BarePlan(), new PlanningSignals
        {
            UserMessage = "What kind of new techniques are we talking about here?",
        });

        var admit = Assert.Single(outcome.Plan.Items, i => i.Policy == ExpressionPolicy.admit_unknown);
        Assert.Equal("executive-admit", admit.Provenance?.Origin);
    }

    // ---- greetings and outreach: no Stheno for the routed user ------------------------------

    private sealed class ThrowingGreeter : IGreeter, IGreetingRephraser
    {
        public Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
            => throw new InvalidOperationException("Stheno greeter invoked for the routed user.");

        public Task<Greeting> RephraseAsync(
            Greeting grounded, string? userId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Stheno rephraser invoked for the routed user.");
    }

    [Fact]
    public async Task TheRoutedUsersGreetingRephrase_NeverReachesTheModelGreeter()
    {
        var routed = new SthenoFreeGreeter(
            new ThrowingGreeter(), new ThrowingGreeter(), deterministic: null!,
            Options.Create(new CompanionOptions
            {
                SthenoFree = new SthenoFreeOptions { Enabled = true, UserId = "demo-user" },
            }));

        var grounded = new Greeting { Message = "It's been 2 hours. Good to see you back." };
        var result = await routed.RephraseAsync(grounded, "demo-user");
        Assert.Same(grounded, result);   // untouched, and the throwing inner proves untouched-by-model

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routed.RephraseAsync(grounded, "someone-else"));
    }

    [Fact]
    public async Task TheRoutedUsersOutreach_IsNeverRestyledByTheModel()
    {
        var rephraser = new SthenoFreeVoiceRephraser(
            new ThrowingVoiceRephraser(),
            Options.Create(new CompanionOptions
            {
                SthenoFree = new SthenoFreeOptions { Enabled = true, UserId = "demo-user" },
            }));

        Assert.Equal("Good luck with the interview today.",
            await rephraser.RephraseAsync("demo-user", "Good luck with the interview today.", "outreach"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rephraser.RephraseAsync("someone-else", "draft", "outreach"));
    }

    private sealed class ThrowingVoiceRephraser : IVoiceRephraser
    {
        public Task<string> RephraseAsync(
            string userId, string draft, string situation, CancellationToken ct = default)
            => throw new InvalidOperationException("Stheno voice rephraser invoked.");
    }

    private sealed class NoDiagnostics : IDiagnosticsStore
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
}
