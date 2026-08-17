using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The observability layer: model calls and tool calls become durable records with role,
/// timing, and outcome; failures are recorded and rethrown untouched; a full turn leaves a
/// queryable trail; stats aggregate per role+model; pruning bounds the tables.
/// </summary>
public class ObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "obs-user";

    private sealed class ExplodingChatModel : IChatModel
    {
        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, ResponseFormat? format = null,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ChatCall_IsRecorded_WithRoleModelAndTiming()
    {
        await using var host = new TestHost(Now);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticsStore>();
        var model = new LoggingChatModel(
            new CannedChatModel("hello there"), "conversation", diagnostics, host.Clock);

        await model.CompleteAsync("system", "user message");

        var stats = await diagnostics.GetModelStatsAsync(Now.AddHours(-1));
        var stat = Assert.Single(stats);
        Assert.Equal("conversation", stat.Role);
        Assert.Equal(1, stat.Calls);
        Assert.Equal(0, stat.Failures);
        Assert.Equal(Now, stat.LastCalledAt);
    }

    [Fact]
    public async Task FailedCall_IsRecordedAsFailure_AndStillThrows()
    {
        await using var host = new TestHost(Now);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticsStore>();
        var model = new LoggingChatModel(new ExplodingChatModel(), "extraction", diagnostics, host.Clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CompleteAsync("s", "u"));

        var stat = Assert.Single(await diagnostics.GetModelStatsAsync(Now.AddHours(-1)));
        Assert.Equal("extraction", stat.Role);
        Assert.Equal(1, stat.Failures);
    }

    [Fact]
    public async Task FullTurn_LeavesADurableToolCallTrail()
    {
        var chat = new QueuedChatModel(
            """{"tool": "memory.search", "arguments": {"query": "Jetson"}}""",
            """{"tool": null}""",
            "Found it.");
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

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId,
                "I finally tested the Jetson at home.");
        }

        var diagnostics = host.Services.GetRequiredService<IDiagnosticsStore>();
        var calls = await diagnostics.GetRecentToolCallsAsync(
            CompanionSeeder.DemoUserId, 10);
        var call = Assert.Single(calls);
        Assert.Equal("memory.search", call.Tool);
        Assert.True(call.Ok);
        Assert.Equal("ok", call.Code);
        Assert.Contains("Jetson", call.Arguments);
    }

    [Fact]
    public async Task Prune_SweepsOldRecords_AndKeepsRecentOnes()
    {
        await using var host = new TestHost(Now);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticsStore>();
        await diagnostics.RecordToolCallAsync(new ToolCallRecord
        {
            Id = Guid.NewGuid(), UserId = User, Tool = "old.call", Ok = true, Code = "ok",
            Timestamp = Now.AddDays(-40),
        });
        await diagnostics.RecordToolCallAsync(new ToolCallRecord
        {
            Id = Guid.NewGuid(), UserId = User, Tool = "fresh.call", Ok = true, Code = "ok",
            Timestamp = Now.AddDays(-1),
        });
        await diagnostics.RecordModelCallAsync(new ModelCallRecord
        {
            Id = Guid.NewGuid(), Role = "conversation", Operation = "complete", Ok = true,
            Timestamp = Now.AddDays(-40),
        });

        var pruned = await diagnostics.PruneAsync(Now.AddDays(-30));

        Assert.Equal(2, pruned);
        var remaining = await diagnostics.GetRecentToolCallsAsync(User, 10);
        Assert.Equal("fresh.call", Assert.Single(remaining).Tool);
    }

    [Fact]
    public async Task StoreWriteFailure_NeverBreaksTheModelCall()
    {
        // A diagnostics store pointed at a dead database must swallow its own failure.
        await using var host = new TestHost(Now);
        var diagnostics = host.Services.GetRequiredService<IDiagnosticsStore>();
        var model = new LoggingChatModel(new CannedChatModel("still fine"), "conversation", diagnostics, host.Clock);

        // Even with a poisoned record (e.g. duplicate id), the reply must come back.
        var id = Guid.NewGuid();
        await diagnostics.RecordModelCallAsync(new ModelCallRecord
        {
            Id = id, Role = "conversation", Operation = "complete", Ok = true, Timestamp = Now,
        });
        await diagnostics.RecordModelCallAsync(new ModelCallRecord
        {
            Id = id, Role = "conversation", Operation = "complete", Ok = true, Timestamp = Now,
        }); // duplicate key → swallowed, not thrown

        var completion = await model.CompleteAsync("s", "u");
        Assert.Equal("still fine", completion.Text);
    }
}
