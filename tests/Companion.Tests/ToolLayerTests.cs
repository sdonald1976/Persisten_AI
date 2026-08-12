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
/// The tool layer end to end: discovery, per-tool behavior, the bounded loop's safety properties
/// (dedupe, max calls, unknown-tool refusal, malformed output), and the full-turn integration —
/// results reach the packet, diagnostics record the calls, and the conversation record stays
/// truthful (no fabricated messages, no tool artifacts in durable memory).
/// </summary>
public class ToolLayerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ExpectedTools =
    {
        "capability.list", "memory.search", "project.get", "openloop.list",
        "procedure.search", "preference.list", "diagnostics.last_turn",
    };

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string DataJson(ToolResult result) => JsonSerializer.Serialize(result.Data);

    // ---- discovery ----

    [Fact]
    public async Task AllSevenReadOnlyTools_AreRegisteredAndAvailable()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var tools = scope.ServiceProvider.GetServices<ICompanionTool>().ToList();

        Assert.Equal(ExpectedTools.OrderBy(n => n), tools.Select(t => t.Name).OrderBy(n => n));
        Assert.All(tools, t => Assert.True(t.Available));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.ArgumentsHint)));
    }

    // ---- individual tools ----

    [Fact]
    public async Task CapabilityList_ReportsTheToolSetItself()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var tool = scope.ServiceProvider.GetServices<ICompanionTool>().Single(t => t.Name == "capability.list");

        var result = await tool.ExecuteAsync(CompanionSeeder.DemoUserId, Args("{}"));

        Assert.True(result.Ok);
        var json = DataJson(result);
        // Honest self-knowledge: every invocable tool is in the answer.
        foreach (var name in ExpectedTools)
            Assert.Contains(name, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemorySearch_FindsSeededMemories_WithProvenance()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var tool = scope.ServiceProvider.GetServices<ICompanionTool>().Single(t => t.Name == "memory.search");

        var result = await tool.ExecuteAsync(
            CompanionSeeder.DemoUserId, Args("""{"query": "Jetson object detection", "limit": 3}"""));

        Assert.True(result.Ok);
        var json = DataJson(result);
        Assert.Contains("Jetson", json, StringComparison.OrdinalIgnoreCase);
        // Results carry provenance labels, not bare strings.
        Assert.Contains("\"kind\"", json, StringComparison.Ordinal);
        Assert.Contains("\"owner\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemorySearch_WithoutQuery_FailsAsInvalidArguments()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var tool = scope.ServiceProvider.GetServices<ICompanionTool>().Single(t => t.Name == "memory.search");

        // Missing, wrong-typed, and empty queries all fail the same controlled way — never a throw.
        foreach (var bad in new[] { "{}", """{"query": 7}""", """{"query": "   "}""" })
        {
            var result = await tool.ExecuteAsync(CompanionSeeder.DemoUserId, Args(bad));
            Assert.False(result.Ok);
            Assert.Equal("invalid_arguments", result.Code);
        }
    }

    [Fact]
    public async Task DiagnosticsTool_WithNoRecordedTurns_ReturnsNotFound()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var tool = scope.ServiceProvider.GetServices<ICompanionTool>().Single(t => t.Name == "diagnostics.last_turn");

        var result = await tool.ExecuteAsync(CompanionSeeder.DemoUserId, Args("{}"));

        Assert.False(result.Ok);
        Assert.Equal("not_found", result.Code);
    }

    // ---- the bounded loop ----

    private sealed class FakeTool : ICompanionTool
    {
        public int Executions { get; private set; }
        public string Name => "fake.lookup";
        public string Description => "A test lookup.";
        public string ArgumentsHint => "{\"query\": \"text\"}";
        public bool Available => true;

        public Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
        {
            Executions++;
            var query = arguments.TryGetProperty("query", out var q) ? q.GetString() : null;
            return query is null
                ? Task.FromResult(ToolResult.Fail("invalid_arguments", "Provide a query."))
                : Task.FromResult(ToolResult.Success(new { query, answer = "forty-two" }));
        }
    }

    private static ToolLoop Loop(IChatModel chat, FakeTool tool, Action<CompanionOptions>? configure = null)
    {
        var options = new CompanionOptions();
        configure?.Invoke(options);
        return new ToolLoop(
            new ICompanionTool[] { tool }, chat, Options.Create(options), NullLogger<ToolLoop>.Instance);
    }

    [Fact]
    public async Task ToolLoop_ExecutesRequestedCall_ThenStopsWhenModelDeclines()
    {
        var chat = new QueuedChatModel(
            """{"tool": "fake.lookup", "arguments": {"query": "meaning"}}""",
            """{"tool": null}""");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool).RunAsync("u1", "context", "message");

        Assert.Equal(1, tool.Executions);
        Assert.Equal(2, chat.Calls);
        var call = Assert.Single(outcome.Calls);
        Assert.True(call.Ok);
        Assert.Equal("ok", call.Code);
        Assert.Contains("forty-two", outcome.ResultsSection);
        Assert.Contains("[fake.lookup]", outcome.ResultsSection);
        // The second decision prompt showed the model what it already looked up.
        Assert.Contains("Results you already looked up", chat.SystemPrompts[1]);
        Assert.Equal(new[] { "fake.lookup" }, outcome.AdvertisedTools);
    }

    [Fact]
    public async Task ToolLoop_IdenticalRepeatCall_StopsInsteadOfLooping()
    {
        var same = """{"tool": "fake.lookup", "arguments": {"query": "meaning"}}""";
        var chat = new QueuedChatModel(same, same, same);
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool).RunAsync("u1", "context", "message");

        Assert.Equal(1, tool.Executions);
        Assert.Single(outcome.Calls);
    }

    [Fact]
    public async Task ToolLoop_HonorsMaxCallsPerTurn()
    {
        var chat = new QueuedChatModel(
            """{"tool": "fake.lookup", "arguments": {"query": "one"}}""",
            """{"tool": "fake.lookup", "arguments": {"query": "two"}}""",
            """{"tool": "fake.lookup", "arguments": {"query": "three"}}""",
            """{"tool": "fake.lookup", "arguments": {"query": "four"}}""");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool, o => o.MaxToolCallsPerTurn = 2).RunAsync("u1", "ctx", "msg");

        Assert.Equal(2, tool.Executions);
        Assert.Equal(2, outcome.Calls.Count);
    }

    [Fact]
    public async Task ToolLoop_UnknownTool_IsRefusedAndRecorded()
    {
        // The model asks for something this installation doesn't have — nothing executes,
        // the refusal is in the trace, and no results section reaches the prompt.
        var chat = new QueuedChatModel("""{"tool": "file.delete", "arguments": {"path": "/etc"}}""");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "msg");

        Assert.Equal(0, tool.Executions);
        var call = Assert.Single(outcome.Calls);
        Assert.False(call.Ok);
        Assert.Equal("unavailable", call.Code);
        Assert.Equal("file.delete", call.Tool);
        Assert.Null(outcome.ResultsSection);
    }

    [Fact]
    public async Task ToolLoop_WhenDisabled_NeverCallsTheModel()
    {
        var chat = new QueuedChatModel("""{"tool": "fake.lookup", "arguments": {"query": "x"}}""");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool, o => o.EnableToolUse = false).RunAsync("u1", "ctx", "msg");

        Assert.Equal(0, chat.Calls);
        Assert.Empty(outcome.Calls);
        Assert.Null(outcome.ResultsSection);
    }

    [Fact]
    public async Task ToolLoop_NonJsonDecision_MeansAnswerDirectly()
    {
        var chat = new QueuedChatModel("Sure! I'd use fake.lookup for that.");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "msg");

        Assert.Equal(0, tool.Executions);
        Assert.Empty(outcome.Calls);
        Assert.Null(outcome.ResultsSection);
    }

    [Fact]
    public async Task ToolLoop_FailedToolCall_IsTracedAndTheLoopMovesOn()
    {
        var chat = new QueuedChatModel(
            """{"tool": "fake.lookup", "arguments": {}}""",
            """{"tool": null}""");
        var tool = new FakeTool();

        var outcome = await Loop(chat, tool).RunAsync("u1", "ctx", "msg");

        var call = Assert.Single(outcome.Calls);
        Assert.False(call.Ok);
        Assert.Equal("invalid_arguments", call.Code);
        // The failure is visible to the model (so it can correct itself) — as data, not a crash.
        Assert.Contains("invalid_arguments", outcome.ResultsSection);
    }

    // ---- full-turn integration ----

    [Fact]
    public async Task FullTurn_ToolResultsReachThePacket_DiagnosticsRecord_ConversationStaysTruthful()
    {
        // Scripted brain: round 1 decides to search memory, round 2 declines, then the reply.
        var chat = new QueuedChatModel(
            """{"tool": "memory.search", "arguments": {"query": "Jetson"}}""",
            """{"tool": null}""",
            "The Jetson test — that's great news. How did the detection hold up at home?");
        await using var host = new TestHost(
            Now, configureServices: s => s.AddSingleton<IChatModel>(chat));

        Guid conversationId;
        using (var seedScope = host.CreateScope())
        {
            var sp = seedScope.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            conversationId = (await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
        }

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");
        }

        // The lookup ran and its results were injected into THIS turn's packet.
        var call = Assert.Single(trace.ToolCalls);
        Assert.True(call.Ok);
        Assert.Equal("memory.search", call.Tool);
        Assert.NotNull(trace.Packet.ToolResults);
        Assert.Contains("[memory.search]", trace.Packet.ToolResults);
        Assert.Contains(Prompts.Get("renderer.tools.header"), trace.Packet.Render());
        Assert.Equal(ExpectedTools.OrderBy(n => n), trace.AdvertisedTools.OrderBy(n => n));

        using (var verify = host.CreateScope())
        {
            var sp = verify.ServiceProvider;

            // Conversation truth: exactly the user message and the final reply — tool calls never
            // become messages.
            var messages = await sp.GetRequiredService<IConversationStore>()
                .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 20);
            Assert.Equal(2, messages.Count);
            Assert.Contains(messages, m => m.Content.Contains("How did the detection hold up"));

            // Privacy/memory truth: nothing derived from the tool plumbing became durable memory.
            var memories = await sp.GetRequiredService<IMemoryStore>()
                .GetRetrievableMemoriesAsync(CompanionSeeder.DemoUserId);
            Assert.DoesNotContain(memories, m =>
                m.Content.Contains("memory.search", StringComparison.OrdinalIgnoreCase)
                || m.Content.Contains("\"tool\"", StringComparison.OrdinalIgnoreCase));

            // The diagnostics ring recorded the turn: sections, tools advertised, calls made.
            var recorded = sp.GetRequiredService<ITurnTraceLog>().Recent(CompanionSeeder.DemoUserId, 1);
            var turn = Assert.Single(recorded);
            Assert.StartsWith("I finally tested", turn.UserMessagePreview);
            Assert.Contains("toolResults", turn.ContextSections);
            Assert.Single(turn.ToolCalls);
            Assert.Equal(ExpectedTools.Length, turn.AdvertisedTools.Count);

            // And diagnostics.last_turn can now answer "why did you say that?" from that record.
            var diagnostics = sp.GetServices<ICompanionTool>().Single(t => t.Name == "diagnostics.last_turn");
            var result = await diagnostics.ExecuteAsync(CompanionSeeder.DemoUserId, Args("{}"));
            Assert.True(result.Ok);
            var json = DataJson(result);
            Assert.Contains("I finally tested", json);
            Assert.Contains("memory.search", json);
        }
    }

    [Fact]
    public async Task FullTurn_ModelNeverAsksForTools_BehavesExactlyAsBefore()
    {
        // The mock chat model answers with prose, never JSON — so the loop decides "answer
        // directly" and the turn is indistinguishable from the pre-tool-layer behavior.
        await using var host = new TestHost(Now);

        Guid conversationId;
        using (var seedScope = host.CreateScope())
        {
            var sp = seedScope.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            conversationId = (await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
        }

        using var scope = host.CreateScope();
        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
            CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");

        Assert.Equal(TurnStatus.Answered, trace.Status);
        Assert.Empty(trace.ToolCalls);
        Assert.Null(trace.Packet.ToolResults);
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));
        // Tools were still advertised (they exist), just not used.
        Assert.Equal(ExpectedTools.Length, trace.AdvertisedTools.Count);
    }
}
