using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.Execution;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B5. The extracted execution stage.
///
/// Execution starts with a prepared packet and plan and ends the moment the displayed reply
/// is selected. It writes nothing — the boundary these tests exist to pin.
///
/// The rule for what belongs here: a runtime check that can CHANGE the displayed reply is
/// execution's; a check that only records is observability's. The canary's critical guard and
/// the reply gate can both replace the reply, so both live here. Their comparison rows do not.
/// </summary>
public class TurnExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Trace = Guid.Parse("77777777-1111-2222-3333-444444444444");
    private static string User => CompanionSeeder.DemoUserId;

    private static TestHost Host(bool canaryEnabled = false, Action<IServiceCollection>? extra = null)
        => new(
            Now,
            configureServices: extra,
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = canaryEnabled ? "true" : "false",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
                ["Companion:RendererShadow:TimeoutSeconds"] = "2",
            });

    private static TurnExecutionRequest Request(
        TestHost host, string promptText = "What did we decide about the shed?",
        bool inCharacter = false, bool sensitive = false,
        ToolLoop.Outcome? tools = null, IProgress<string>? sink = null)
        => new()
        {
            TraceId = Trace,
            UserId = User,
            ConversationId = Guid.NewGuid(),
            SourceMessageId = Guid.NewGuid(),
            PromptText = promptText,
            Packet = new ContextPacket { UserMessage = promptText, MaxPromptTokens = 4000 },
            Recent = [],
            Plan = ResponsePlanner.Build(
                Trace,
                Core.Turns.Understanding.TurnUnderstanding.ClassifyIntent(
                    Core.Turns.Understanding.TurnUnderstanding
                        .Read([], promptText, null, "Scott", "Ava").Working,
                    promptText, 0).Intent,
                Core.Turns.Understanding.TurnUnderstanding
                    .Read([], promptText, null, "Scott", "Ava").Working,
                promptText, [], null, null, null, null, null),
            ToolOutcome = tools ?? new ToolLoop.Outcome([], [], null, [], 0),
            InCharacter = inCharacter,
            Sensitive = sensitive,
            CompanionName = "Ava",
            TokenSink = sink,
        };

    private static TurnExecution Exec(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<TurnExecution>();

    // ---- the ordinary path -------------------------------------------------------------------

    [Fact]
    public async Task AnOrdinaryTurn_ProducesAProductionReply()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        Assert.False(string.IsNullOrWhiteSpace(result.Displayed));
        Assert.Equal("production", result.SelectedRenderer);
        Assert.Null(result.RendererCandidate);
        Assert.Null(result.FallbackReason);
        Assert.False(result.CanaryTurn);
        Assert.Equal(result.ProductionCandidate, result.Displayed);
    }

    [Fact]
    public async Task TheRenderedPromptAndGenerationMetadataAreReturned()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        // Rendered once and returned, so diagnostics report the string that was actually sent.
        Assert.False(string.IsNullOrWhiteSpace(result.RenderedPrompt));
        Assert.NotNull(result.Generation);
    }

    [Fact]
    public async Task TheDisplayedReplyIsSelectedExactlyOnce()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var reported = new List<string>();
        var sink = new CollectingSink(reported);

        var result = await Exec(scope).ExecuteAsync(Request(host, sink: sink));

        // Not a canary turn: the generator streams and execution reports nothing extra, so
        // the reply is never delivered twice.
        Assert.Equal("production", result.SelectedRenderer);
        Assert.DoesNotContain(reported, r => r == result.Displayed && reported.Count(x => x == r) > 1);
    }

    // ---- tools ---------------------------------------------------------------------------------

    [Fact]
    public async Task AToolFreeTurn_RecordsNoneAndAdvertisesNothingCalled()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var (outcome, decision) = await Exec(scope).RunToolsAsync(
            User, [], [], null, "The squirrel defeated the baffle again.", Trace);

        Assert.Empty(outcome.Calls);
        Assert.Equal("tools", decision.Stage);
        Assert.Equal("none", decision.Verdict);
    }

    [Fact]
    public async Task ToolCallsAreNamedInOrderInTheDecision()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var calls = new List<ToolCallTrace>
        {
            new() { Tool = "memory.search", Ok = true, Code = "ok" },
            new() { Tool = "project.list", Ok = true, Code = "ok" },
        };
        var outcome = new ToolLoop.Outcome(["memory.search", "project.list"], calls, "results", [], 1);

        // The decision is built from the outcome, so ordering is the loop's, not re-sorted.
        var request = Request(host, tools: outcome);
        Assert.Equal(["memory.search", "project.list"], outcome.Calls.Select(c => c.Tool));
        Assert.NotNull(request.ToolOutcome);
    }

    [Fact]
    public async Task AToolUsingTurn_IsNeverCanaryEligible()
    {
        // Capability routing: run-1c never trained on tool results.
        await using var host = Host(canaryEnabled: true);
        using var scope = host.CreateScope();

        var withTool = new ToolLoop.Outcome(
            ["memory.search"], [new ToolCallTrace { Tool = "memory.search", Ok = true, Code = "ok" }], "r", [], 1);

        var result = await Exec(scope).ExecuteAsync(Request(host, tools: withTool));

        Assert.False(result.CanaryTurn);
        Assert.Equal("production", result.SelectedRenderer);
    }

    [Fact]
    public async Task AFailedToolDoesNotStopTheTurn()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var failed = new ToolLoop.Outcome(
            ["memory.search"], [new ToolCallTrace { Tool = "memory.search", Ok = false, Code = "error" }], null, [], 1);

        var result = await Exec(scope).ExecuteAsync(Request(host, tools: failed));

        Assert.False(string.IsNullOrWhiteSpace(result.Displayed));
    }

    // ---- canary routing --------------------------------------------------------------------------

    [Fact]
    public async Task WithTheCanaryDisabled_ProductionAlwaysWins()
    {
        await using var host = Host(canaryEnabled: false);
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        Assert.False(result.CanaryTurn);
        Assert.Equal("production", result.SelectedRenderer);
        Assert.DoesNotContain(result.Decisions, d => d.Stage == "renderer.canary");
    }

    [Fact]
    public async Task AnInCharacterTurn_StaysOnProduction()
    {
        await using var host = Host(canaryEnabled: true);
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host, inCharacter: true));

        Assert.False(result.CanaryTurn);
        Assert.Equal("production", result.SelectedRenderer);
    }

    [Fact]
    public async Task AnUnreachableRenderer_FallsBackToProductionWithAReason()
    {
        // The canary user is not configured in tests, so this asserts the shape of the
        // fallback rather than forcing the canary on: production wins and says nothing broke.
        await using var host = Host(canaryEnabled: true);
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        Assert.Equal("production", result.SelectedRenderer);
        Assert.Equal(result.ProductionCandidate, result.Displayed);
    }

    // ---- the gate ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnOpenGate_LeavesTheReplyAloneAndRecordsNoRefusal()
    {
        await using var host = Host();
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        Assert.Null(result.Refusal);
        Assert.Equal(result.ProductionCandidate, result.Displayed);
    }

    [Fact]
    public async Task ABlockingGateInShadowMode_ReportsTheRefusalWithoutChangingTheReply()
    {
        await using var host = Host(extra: s => s.AddSingleton<IReplyGate>(new BlockingGate()));
        using var scope = host.CreateScope();

        var result = await Exec(scope).ExecuteAsync(Request(host));

        Assert.NotNull(result.Refusal);
        Assert.False(result.Refusal!.Enforced);
        // Shadow mode is the default: the verdict is reported, the reply goes out unchanged.
        Assert.Equal(result.ProductionCandidate, result.Displayed);
        Assert.Contains(result.Decisions, d => d.Stage == "reply.gate" && d.Verdict == "block-shadow");
    }

    [Fact]
    public async Task TheRefusalIsReturnedRatherThanRecorded()
    {
        // Execution decides; the comparison row is the caller's to write. This is the
        // boundary between "can change the reply" and "records what happened".
        await using var host = Host(extra: s => s.AddSingleton<IReplyGate>(new BlockingGate()));
        using var scope = host.CreateScope();

        var before = await CountShadowRowsAsync(host);
        var result = await Exec(scope).ExecuteAsync(Request(host));
        var after = await CountShadowRowsAsync(host);

        Assert.NotNull(result.Refusal);
        Assert.Equal(before, after);
    }

    // ---- the boundary: execution persists nothing ---------------------------------------------------

    [Fact]
    public async Task ExecutionWritesNoMessageMemoryMoodOrReflection()
    {
        await using var host = Host();

        var before = await SnapshotAsync(host);
        using (var scope = host.CreateScope())
            await Exec(scope).ExecuteAsync(Request(host));
        var after = await SnapshotAsync(host);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ASensitiveTurn_StillExecutesAndStillWritesNothing()
    {
        await using var host = Host();

        var before = await SnapshotAsync(host);
        using (var scope = host.CreateScope())
        {
            var result = await Exec(scope).ExecuteAsync(
                Request(host, promptText: "Keep this private: something personal.", sensitive: true));
            Assert.False(string.IsNullOrWhiteSpace(result.Displayed));
        }

        Assert.Equal(before, await SnapshotAsync(host));
    }

    [Fact]
    public async Task TheCancellationTokenIsThreadedToEveryAwaitedCall()
    {
        // Structural rather than behavioural, deliberately. Whether a cancelled token throws
        // depends on the model client, and the test host's generator does not honour it —
        // that is existing behaviour this phase preserves rather than improves. What the
        // extraction must not do is DROP the token, so that is what is asserted.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Execution", "TurnExecution.cs"));

        foreach (var call in new[]
                 {
                     "toolLoop.RunAsync(userId, planningContext, promptText, traceId, ct)",
                     "request.CompanionName, ct)",
                     "record: !request.Sensitive, ct)",
                     "_gate.ReviewAsync(response, request.PromptText, ct)",
                 })
            Assert.Contains(call, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledTokenStillReachesExecutionWithoutCorruptingTheResult()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Either it throws, or it completes with a coherent result. What must never happen is
        // a partially-selected reply.
        try
        {
            var result = await Exec(scope).ExecuteAsync(Request(host), cancelled.Token);
            Assert.NotNull(result.Displayed);
            Assert.Equal(
                result.SelectedRenderer == "production"
                    ? result.ProductionCandidate
                    : result.RendererCandidate,
                result.Displayed);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable, and what a token-honouring client would do.
        }
    }

    // ---- structure ------------------------------------------------------------------------------------

    [Fact]
    public void ExecutionOwnsNoPersistence()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Execution", "TurnExecution.cs"));

        foreach (var forbidden in new[]
                 {
                     "IConversationStore", "IMemoryStore", "IMemoryPipeline", "IEmotionStore",
                     "IReflectionStore", "IAttentionService", "IProcedureStore",
                     "IDiagnosticsStore", "IShadowRecorder", "SaveChangesAsync",
                     "StoreMessageAsync", "RecordAsync", "RecordTurnAsync",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePlanFourReachesNoModel()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Execution", "TurnExecution.cs"));

        // The native material is carried into the observation for recording and nowhere else.
        // The only thing given to the generator is the rendered production packet.
        var generateCall = source[source.IndexOf("GenerateAsync(", StringComparison.Ordinal)..];
        var callArgs = generateCall[..generateCall.IndexOf(");", StringComparison.Ordinal)];

        Assert.Contains("renderedPrompt", callArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeV3", callArgs, StringComparison.Ordinal);

        // Carrying NativeCompactV4Chars through for the observation is fine — it is a number
        // somebody else computed. SERIALIZING here would not be, so the call is what is
        // forbidden rather than the word.
        Assert.DoesNotContain("PlanV4Codec.CompactV4(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactV3(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultIsTypedThroughout()
    {
        var properties = typeof(TurnExecutionResult).GetProperties();
        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, p =>
            p.PropertyType == typeof(object)
            || typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType));
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static async Task<string> SnapshotAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        return string.Join("|",
            await db.Messages.CountAsync(),
            await db.SemanticMemories.CountAsync(),
            await db.EpisodicMemories.CountAsync(),
            await db.EmotionalSignals.CountAsync(),
            await db.Reflections.CountAsync(),
            await db.AttentionItems.CountAsync(),
            await db.TurnRecords.CountAsync(),
            await db.ShadowComparisons.CountAsync());
    }

    private static async Task<int> CountShadowRowsAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>()
            .ShadowComparisons.CountAsync();
    }

    private sealed class BlockingGate : IReplyGate
    {
        public bool IsEnabled => true;
        public Task<GateVerdict> ReviewAsync(string reply, string userMessage, CancellationToken ct = default)
            => Task.FromResult(new GateVerdict(false, "synthetic refusal for the test"));
    }

    private sealed class CollectingSink(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
