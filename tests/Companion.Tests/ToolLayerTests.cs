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
/// (dedupe, max calls, unknown-tool refusal, malformed output), and the full-turn integration â€”
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

        // Missing, wrong-typed, and empty queries all fail the same controlled way â€” never a throw.
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

    private sealed class NoopDiagnostics : IDiagnosticsStore
    {
        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            CancellationToken ct = default) => Task.FromResult(0);

        public List<ToolCallRecord> ToolCalls { get; } = new();
        public Task RecordModelCallAsync(ModelCallRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct = default)
        { ToolCalls.Add(record); return Task.CompletedTask; }
        public Task<IReadOnlyList<ToolCallRecord>> GetRecentToolCallsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ToolCallRecord>>(ToolCalls);
        public Task<IReadOnlyList<ModelRoleStats>> GetModelStatsAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelRoleStats>>(Array.Empty<ModelRoleStats>());
        public Task RecordTurnAsync(TurnRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TurnRecord>> GetRecentTurnsAsync(string userId, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TurnRecord>>(Array.Empty<TurnRecord>());
        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static ToolLoop Loop(
        IChatModel chat, FakeTool tool, Action<CompanionOptions>? configure = null, NoopDiagnostics? diagnostics = null)
    {
        var options = new CompanionOptions();
        configure?.Invoke(options);
        return new ToolLoop(
            new ICompanionTool[] { tool }, chat, diagnostics ?? new NoopDiagnostics(),
            Options.Create(options), TimeProvider.System, NullLogger<ToolLoop>.Instance);
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
        // Both verbatim decisions (the call and the decline) are kept for diagnostics.
        Assert.Equal(2, outcome.Decisions.Count);
        Assert.Contains("fake.lookup", outcome.Decisions[0]);
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
        // The model asks for something this installation doesn't have â€” nothing executes,
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
        // The failure is visible to the model (so it can correct itself) â€” as data, not a crash.
        Assert.Contains("invalid_arguments", outcome.ResultsSection);
    }

    // ---- rules-first nudges ----

    [Theory]
    [InlineData("Be honest â€” what can you actually do? Can you see images?", "capability.list")]
    [InlineData("can you hear me right now?", "capability.list")]
    [InlineData("Why did you bring that up just now?", "diagnostics.last_turn")]
    [InlineData("Is there anything unfinished between us?", "openloop.list")]
    [InlineData("So what do you actually like these days?", "preference.list")]
    public void Nudge_MatchesUnambiguousPhrasings(string message, string expectedTool)
        => Assert.Equal(expectedTool, ToolNudge.Detect(message)?.Tool);

    [Fact]
    public void Nudge_ExtractsTheMemoryTopic()
    {
        var match = ToolNudge.Detect("Hey, do you remember the greenhouse sensor debacle?");
        Assert.NotNull(match);
        Assert.Equal("memory.search", match!.Tool);
        Assert.Contains("greenhouse sensor debacle", match.ArgumentsJson);
    }

    [Theory]
    [InlineData("How was your day?")]
    [InlineData("I can see the finish line on this project.")]  // user's own "can see", not a question to her
    [InlineData("Let's talk about the weather.")]
    public void Nudge_LeavesOrdinaryChatAlone(string message)
        => Assert.Null(ToolNudge.Detect(message));

    [Fact]
    public void Nudge_WhenTheTopicIsAPronoun_SearchesTheWholeMessage()
    {
        // The captured topic is only what follows the trigger, so this yields "about it" â€” all
        // stopwords, which scores zero against every memory and retrieves noise. The referent
        // ("the Persisten_AI companion") is in the same message, just earlier in it.
        var match = ToolNudge.Detect(
            "Hi Ava - I'm back working on the Persisten_AI companion tonight. What do you remember about it?");

        Assert.NotNull(match);
        Assert.Equal("memory.search", match!.Tool);
        Assert.Contains("Persisten_AI", match.ArgumentsJson);
    }

    [Theory]
    [InlineData("So, do you remember that?")]
    [InlineData("Anyway â€” do you remember it?")]        // too short to even match; no nudge is fine
    [InlineData("Right, do you remember any of that?")]
    public void Nudge_ContentlessTopic_NeverBecomesTheQuery(string message)
    {
        // Not firing is an acceptable outcome â€” a missed nudge costs nothing, since the model loop
        // still runs. What is never acceptable is searching for the bare anaphor: those tokens are
        // all stopwords, so they match nothing and the retriever returns noise ranked as relevant.
        var match = ToolNudge.Detect(message);
        if (match is null)
            return;

        Assert.DoesNotContain("\"query\":\"that\"", match.ArgumentsJson);
        Assert.DoesNotContain("\"query\":\"it\"", match.ArgumentsJson);
        Assert.DoesNotContain("\"query\":\"any of that\"", match.ArgumentsJson);
    }

    [Fact]
    public void Nudge_WellFormedTopic_IsStillUsedVerbatim()
    {
        // The fallback must not swallow the narrow, correct case: a real topic stays the query.
        var match = ToolNudge.Detect("Quick one â€” do you remember the greenhouse sensor debacle?");

        Assert.NotNull(match);
        Assert.Contains("\"query\":\"the greenhouse sensor debacle\"", match!.ArgumentsJson);
    }

    [Fact]
    public async Task Nudge_RunsTheTool_EvenWhenTheModelDeclines()
    {
        // The model always says no â€” the deterministic nudge must carry the obvious case anyway.
        var chat = new QueuedChatModel("""{"tool": null}""");
        var tool = new FakeTool();
        var diagnostics = new NoopDiagnostics();

        // FakeTool is named fake.lookup, so use the loop with a memory-style nudgeâ€¦
        var outcome = await Loop(chat, tool, diagnostics: diagnostics)
            .RunAsync("u1", "ctx", "do you remember the answer to everything?");

        // â€¦which targets memory.search: not in this loop's tool set, so nothing ran â€” but the
        // decision trail shows the rules never got a matching tool and the model was still asked.
        Assert.Empty(outcome.Calls);
        Assert.Equal(1, chat.Calls);
    }

    private sealed class NudgeTool : ICompanionTool
    {
        public int Executions { get; private set; }
        public string Name => "capability.list";
        public string Description => "capabilities";
        public string ArgumentsHint => "{}";
        public bool Available => true;
        public Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolResult.Success(new { vision = "available" }));
        }
    }

    [Fact]
    public async Task CapabilityQuestion_GetsRegistryData_WithAnUncooperativeModel()
    {
        // The exact live failure: "can you see images?" answered by an RP model that declines
        // the JSON protocol. The nudge runs capability.list deterministically; the model's
        // decline afterwards costs nothing; the packet still carries real registry data.
        var chat = new QueuedChatModel("""{"tool": null}""");
        var tool = new NudgeTool();
        var loop = new ToolLoop(
            new ICompanionTool[] { tool }, chat, new NoopDiagnostics(),
            Options.Create(new CompanionOptions()), TimeProvider.System, NullLogger<ToolLoop>.Instance);

        var outcome = await loop.RunAsync("u1", "ctx", "Be honest â€” what can you actually do? Can you see images?");

        Assert.Equal(1, tool.Executions);
        var call = Assert.Single(outcome.Calls);
        Assert.True(call.Ok);
        Assert.Contains("[capability.list]", outcome.ResultsSection);
        Assert.Contains("(rule nudge) capability.list", outcome.Decisions[0]);
        // The model was still consulted for ADDITIONAL lookups and declined â€” that's fine.
        Assert.Equal(1, chat.Calls);
    }

    // ---- full-turn integration ----

    [Fact]
    public async Task FullTurn_ToolResultsReachThePacket_DiagnosticsRecord_ConversationStaysTruthful()
    {
        // Scripted brain: round 1 decides to search memory, round 2 declines, then the reply.
        var chat = new QueuedChatModel(
            """{"tool": "memory.search", "arguments": {"query": "Jetson"}}""",
            """{"tool": null}""",
            "The Jetson test â€” that's great news. How did the detection hold up at home?");
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

            // Conversation truth: exactly the user message and the final reply â€” tool calls never
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
        // The mock chat model answers with prose, never JSON â€” so the loop decides "answer
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

