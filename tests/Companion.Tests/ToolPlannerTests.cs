using System.Text.Json;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The executive tool planner: structured multi-call plans, iterative rounds within hard
/// bounds, benign failure, trusted-user enforcement, and the compact planning context (the
/// planner is not her â€” it never sees the persona packet).
/// </summary>
public class ToolPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingTool : ICompanionTool
    {
        private readonly string _name;
        public List<string> UserIds { get; } = new();
        public List<string> Arguments { get; } = new();

        public RecordingTool(string name) => _name = name;

        public string Name => _name;
        public string Description => $"test tool {_name}";
        public string ArgumentsHint => "{}";
        public bool Available => true;

        public Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
        {
            UserIds.Add(userId);
            Arguments.Add(arguments.GetRawText());
            return Task.FromResult(ToolResult.Success(new { from = _name }));
        }
    }

    private sealed class NoopDiagnostics : IDiagnosticsStore
    {
        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult(0);

        public Task RecordModelCallAsync(ModelCallRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallRecord>> GetRecentToolCallsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolCallRecord>>(Array.Empty<ToolCallRecord>());
        public Task<IReadOnlyList<ModelRoleStats>> GetModelStatsAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelRoleStats>>(Array.Empty<ModelRoleStats>());
        public Task RecordTurnAsync(TurnRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TurnRecord>> GetRecentTurnsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TurnRecord>>(Array.Empty<TurnRecord>());
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ThrowingChatModel : IChatModel
    {
        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, ResponseFormat? format = null,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new HttpRequestException("planner endpoint down");

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new HttpRequestException("planner endpoint down");
    }

    private static ToolLoop Loop(IChatModel chat, params ICompanionTool[] tools) =>
        new(tools, chat, new NoopDiagnostics(),
            Options.Create(new CompanionOptions()), TimeProvider.System, NullLogger<ToolLoop>.Instance);

    [Fact]
    public async Task StructuredPlan_ExecutesMultipleCallsInOneRound()
    {
        var chat = new QueuedChatModel(
            """
            {"needsTools": true, "reason": "historical reference plus project context",
             "calls": [
               {"tool": "alpha.search", "arguments": {"query": "board"}},
               {"tool": "beta.get", "arguments": {}}
             ]}
            """,
            """{"needsTools": false, "reason": "enough now", "calls": []}""");
        var alpha = new RecordingTool("alpha.search");
        var beta = new RecordingTool("beta.get");

        var outcome = await Loop(chat, alpha, beta).RunAsync("u1", "ctx", "whatever happened with that board?");

        Assert.Single(alpha.UserIds);
        Assert.Single(beta.UserIds);
        Assert.Equal(2, outcome.Calls.Count);
        Assert.Equal(2, outcome.PlanningRounds);
        Assert.Contains("[alpha.search]", outcome.ResultsSection);
        Assert.Contains("[beta.get]", outcome.ResultsSection);
    }

    [Fact]
    public async Task NeedsToolsFalse_MeansNoCalls_AndOneRound()
    {
        var chat = new QueuedChatModel(
            """{"needsTools": false, "reason": "casual conversation", "calls": []}""");
        var tool = new RecordingTool("alpha.search");

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "How's it going?");

        Assert.Empty(tool.UserIds);
        Assert.Empty(outcome.Calls);
        Assert.Null(outcome.ResultsSection);
        Assert.Equal(1, outcome.PlanningRounds);
    }

    [Fact]
    public async Task IterativePlanning_FollowsUpOnce_ThenStops()
    {
        // Round 1 searches; round 2 chases what the result revealed; a third plan is never
        // requested (MaxPlanningRounds = 2) even though the model has more scripted.
        var chat = new QueuedChatModel(
            """{"needsTools": true, "reason": "gap", "calls": [{"tool": "alpha.search", "arguments": {"query": "x"}}]}""",
            """{"needsTools": true, "reason": "result mentioned beta", "calls": [{"tool": "beta.get", "arguments": {}}]}""",
            """{"needsTools": true, "reason": "should never be asked", "calls": [{"tool": "gamma.list", "arguments": {}}]}""");
        var alpha = new RecordingTool("alpha.search");
        var beta = new RecordingTool("beta.get");
        var gamma = new RecordingTool("gamma.list");

        var outcome = await Loop(chat, alpha, beta, gamma).RunAsync("u1", "ctx", "msg");

        Assert.Single(alpha.UserIds);
        Assert.Single(beta.UserIds);
        Assert.Empty(gamma.UserIds);
        Assert.Equal(2, outcome.PlanningRounds);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task CallBudget_CapsAPlanThatAsksForTooMuch()
    {
        var chat = new QueuedChatModel(
            """
            {"needsTools": true, "reason": "greedy", "calls": [
              {"tool": "alpha.search", "arguments": {"query": "1"}},
              {"tool": "beta.get", "arguments": {}},
              {"tool": "gamma.list", "arguments": {}},
              {"tool": "delta.read", "arguments": {}}
            ]}
            """);
        var tools = new[] { "alpha.search", "beta.get", "gamma.list", "delta.read" }
            .Select(n => new RecordingTool(n)).ToArray();

        // Default MaxToolCallsPerTurn is 3 â€” the fourth call must never run.
        var outcome = await Loop(chat, tools.Cast<ICompanionTool>().ToArray()).RunAsync("u1", "ctx", "msg");

        Assert.Equal(3, outcome.Calls.Count);
        Assert.Empty(tools[3].UserIds);
    }

    [Fact]
    public async Task PlannerFailure_IsBenign_AndRecorded()
    {
        var tool = new RecordingTool("alpha.search");

        var outcome = await Loop(new ThrowingChatModel(), tool).RunAsync("u1", "ctx", "msg");

        Assert.Empty(tool.UserIds);
        Assert.Empty(outcome.Calls);
        Assert.Null(outcome.ResultsSection);
        Assert.Contains(outcome.Decisions, d => d.StartsWith("(planner failure)"));
    }

    [Fact]
    public async Task MalformedPlan_IsBenign_AndRecorded()
    {
        var chat = new QueuedChatModel("I think we should search memory! Let me help with that.");
        var tool = new RecordingTool("alpha.search");

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "msg");

        Assert.Empty(outcome.Calls);
        Assert.Contains(outcome.Decisions, d => d.Contains("unusable"));
    }

    [Fact]
    public async Task Planner_CannotSupplyOrOverrideTheUserId()
    {
        // The plan smuggles a userId argument â€” the tool must still run as the trusted user,
        // and the smuggled value arrives only as an ignorable argument, never as identity.
        var chat = new QueuedChatModel(
            """{"needsTools": true, "reason": "r", "calls": [{"tool": "alpha.search", "arguments": {"userId": "victim-user", "query": "x"}}]}""");
        var tool = new RecordingTool("alpha.search");

        await Loop(chat, tool).RunAsync("trusted-user", "ctx", "msg");

        Assert.Equal("trusted-user", Assert.Single(tool.UserIds));
    }

    [Fact]
    public async Task UnavailableTool_IsSkipped_WhileValidCallsProceed()
    {
        var chat = new QueuedChatModel(
            """
            {"needsTools": true, "reason": "r", "calls": [
              {"tool": "file.delete", "arguments": {}},
              {"tool": "alpha.search", "arguments": {"query": "x"}}
            ]}
            """,
            """{"needsTools": false, "reason": "done", "calls": []}""");
        var tool = new RecordingTool("alpha.search");

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "msg");

        Assert.Single(tool.UserIds);
        Assert.Equal(2, outcome.Calls.Count);
        Assert.Contains(outcome.Calls, c => c.Tool == "file.delete" && c.Code == "unavailable");
        Assert.Contains(outcome.Calls, c => c.Tool == "alpha.search" && c.Ok);
    }

    [Fact]
    public async Task DeterministicHint_IsExecuted_AndShownToThePlanner()
    {
        var chat = new QueuedChatModel("""{"needsTools": false, "reason": "hint covered it", "calls": []}""");
        var capability = new RecordingTool("capability.list");

        var outcome = await Loop(chat, capability).RunAsync(
            "u1", "ctx", "Be honest â€” what can you actually do?");

        // The nudge ran before any planning; the planner saw both the hint and its results.
        Assert.Single(capability.UserIds);
        Assert.Contains("(rule nudge) capability.list", outcome.Decisions[0]);
        Assert.Contains("Deterministic hint", chat.SystemPrompts[0]);
        Assert.Contains("[capability.list]", chat.SystemPrompts[0]);
    }

    [Theory]
    [InlineData("How do I make my ribeye the way I taught you?", "procedure.search")]
    [InlineData("What procedures have I taught you?", "procedure.search")]
    [InlineData("What projects am I working on right now?", "project.get")]
    public void NewNudges_CoverProceduresAndProjects(string message, string expected)
        => Assert.Equal(expected, ToolNudge.Detect(message)?.Tool);

    [Fact]
    public async Task FullTurn_PlannerSeesCompactContext_NeverThePersonaPacket()
    {
        var chat = new QueuedChatModel(
            """{"needsTools": true, "reason": "older reference", "calls": [{"tool": "memory.search", "arguments": {"query": "Jetson"}}]}""",
            """{"needsTools": false, "reason": "covered", "calls": []}""",
            "It went well, if I remember right.");
        await using var host = new TestHost(
            Now, configureServices: s => s.AddSingleton<IChatModel>(chat));

        Guid conversationId;
        using (var seed = host.CreateScope())
        {
            var sp = seed.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            conversationId = (await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
        }

        using var scope = host.CreateScope();
        // Unambiguous project reference on purpose: "that board" would (correctly) pause the
        // turn for clarification before any planning happens â€” that path has its own tests.
        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
            CompanionSeeder.DemoUserId, conversationId,
            "Whatever happened with the Jetson work from months ago?");

        // The planner's prompt is the compact planning viewâ€¦
        var plannerPrompt = chat.SystemPrompts[0];
        Assert.Contains("Recent conversation (newest last):", plannerPrompt);
        Assert.Contains("Automatic retrieval", plannerPrompt);
        Assert.DoesNotContain("## ", plannerPrompt); // no packet sections â€” no persona, no mood

        // â€¦while the final reply model got the full packet WITH the tool results, and the
        // response is still the conversational model's own prose (personality preserved).
        var replyPrompt = chat.SystemPrompts[^1];
        Assert.Contains(Prompts.Get("renderer.tools.header"), replyPrompt);
        Assert.Equal("It went well, if I remember right.", trace.Response);
        Assert.Single(trace.ToolCalls, c => c.Tool == "memory.search" && c.Ok);
        Assert.NotNull(trace.Packet.ToolResults);
    }
}

