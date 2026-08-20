using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase 0 of the language-organ plan (docs/LANGUAGE_ORGAN.md): every turn's system-level
/// decisions are recorded and correlatable. These tests pin the contract the soak harness and
/// the synthetic evaluator read over HTTP — if a field here goes missing, those consumers fail
/// silently, which is exactly the failure mode Phase 0 exists to end.
/// </summary>
public class TurnObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<(TestHost host, Guid conversationId)> SeededSessionAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "session", "mock", "test");
        return (host, conv.Id);
    }

    private static async Task<TurnTrace> SayAsync(TestHost host, Guid conversationId, string message)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conversationId, message);
    }

    [Fact]
    public async Task AnsweredTurn_CarriesOneTraceId_SharedBetweenTraceAndDiagnosticsRing()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        var trace = await SayAsync(host, conversationId, "I tested the Jetson Nano deployment at home.");

        Assert.NotEqual(Guid.Empty, trace.TraceId);

        var ring = host.Services.GetRequiredService<ITurnTraceLog>().Recent(User, 1);
        var turn = Assert.Single(ring);
        Assert.Equal(trace.TraceId, turn.TraceId);
    }

    [Fact]
    public async Task Decisions_RecordEveryPipelineStage_ForAPlainTurn()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        await SayAsync(host, conversationId, "I tested the Jetson Nano deployment at home.");

        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>().Recent(User, 1));

        // Every stage the pipeline decided is present exactly once, in pipeline order.
        string[] expected = ["privacy", "roleplay", "memory.derived", "project",
            "curiosity", "register", "packet.budget", "tools"];
        var stages = turn.Decisions.Select(d => d.Stage).ToList();
        foreach (var stage in expected)
            Assert.Single(turn.Decisions, d => d.Stage == stage);
        Assert.Equal(expected.Where(stages.Contains), stages.Where(expected.Contains));

        // Deterministic verdicts for a plain, rememberable turn against the seeded history.
        Assert.Equal("plain", turn.Decisions.Single(d => d.Stage == "roleplay").Verdict);
        Assert.Equal("enabled", turn.Decisions.Single(d => d.Stage == "memory.derived").Verdict);
        Assert.Equal(CompanionSeeder.JetsonProject,
            turn.Decisions.Single(d => d.Stage == "project").Verdict);

        // Every decision names its decider, and rules never claim a confidence.
        Assert.All(turn.Decisions, d => Assert.Contains(d.Decider, new[] { "rule", "model", "config" }));
        Assert.All(turn.Decisions.Where(d => d.Decider == "rule"), d => Assert.Null(d.Confidence));

        // Extraction ran for a rememberable turn, and its outcome is a decision too.
        Assert.Single(turn.Decisions, d => d.Stage == "extraction");
    }

    [Fact]
    public async Task Retrieved_IsStructured_AndAgreesWithTheProseSummaries()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        await SayAsync(host, conversationId, "I tested the Jetson Nano deployment at home.");

        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>().Recent(User, 1));

        Assert.True(turn.MemoriesRetrieved > 0, "the seeded history should retrieve something");
        Assert.Equal(turn.RetrievedSummaries.Count, turn.Retrieved.Count);
        Assert.All(turn.Retrieved, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Content));
            Assert.Contains(r.Source, new[] { "retrieval", "associative" });
        });

        // The structured entries are the same memories the prose summaries describe.
        foreach (var (summary, structured) in turn.RetrievedSummaries.Zip(turn.Retrieved))
            Assert.StartsWith(structured.Content, summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateConversation_IsVisibleInTheDecisions()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .SetDoNotRememberAsync(conversationId, User, true);
        }

        // A neutral message: the only reason derived memory is off is the conversation flag.
        await SayAsync(host, conversationId, "I had a nice walk this afternoon.");

        var turn = Assert.Single(host.Services.GetRequiredService<ITurnTraceLog>().Recent(User, 1));
        var derived = turn.Decisions.Single(d => d.Stage == "memory.derived");
        Assert.Equal("disabled", derived.Verdict);
        Assert.Equal("do-not-remember conversation", derived.Reason);
        Assert.DoesNotContain(turn.Decisions, d => d.Stage == "extraction");
    }
}
