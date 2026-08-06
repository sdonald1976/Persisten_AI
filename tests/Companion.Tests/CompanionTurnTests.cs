using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class CompanionTurnTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FullTurn_StoresMessages_RetrievesMemories_AndReturnsTrace()
    {
        await using var host = new TestHost(Now);

        Guid conversationId;
        using (var seedScope = host.CreateScope())
        {
            var sp = seedScope.ServiceProvider;
            await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
            var conv = await sp.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");
            conversationId = conv.Id;
        }

        TurnTrace trace;
        using (var scope = host.CreateScope())
        {
            var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();
            // Use an unambiguous reference ("the Jetson") so this exercises a normal answered turn;
            // an ambiguous reference like "that board" now deliberately pauses for clarification,
            // which is covered by AmbiguityControlFlowTests.
            trace = await companion.RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");
        }

        // Retrieval happened and continuity reached the packet.
        Assert.Equal(TurnStatus.Answered, trace.Status);
        Assert.NotEmpty(trace.Retrieved);
        Assert.Contains("Jetson", trace.Packet.Render(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));

        // Both the user message and the assistant reply were persisted.
        using (var verifyScope = host.CreateScope())
        {
            var conversations = verifyScope.ServiceProvider.GetRequiredService<IConversationStore>();
            var messages = await conversations.GetRecentMessagesAsync(
                conversationId, CompanionSeeder.DemoUserId, 10);

            Assert.Contains(messages, m => m.Role == MessageRole.User && m.Content.Contains("tested"));
            Assert.Contains(messages, m => m.Role == MessageRole.Assistant);
            // The assistant reply links back to the user message it answered.
            var assistant = messages.Single(m => m.Role == MessageRole.Assistant);
            Assert.NotNull(assistant.ReplyToId);
        }
    }

    [Fact]
    public async Task EmptyMessage_IsRejected()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            companion.RespondAsync(CompanionSeeder.DemoUserId, Guid.NewGuid(), "   "));
    }
}
