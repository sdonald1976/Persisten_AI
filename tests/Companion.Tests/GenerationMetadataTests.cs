using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Every model-produced reply is stored with the signals around it — how it stopped, how many
/// rounds it took, which model — so "why did it stop / how was this produced" is answerable after
/// the fact rather than a mystery. User messages carry none of it.
/// </summary>
public class GenerationMetadataTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AssistantReply_RecordsGenerationMetadata()
    {
        await using var host = new TestHost(Now);
        Guid conversationId;
        using (var scope = host.CreateScope())
        {
            var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");
            conversationId = conv.Id;
        }

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(CompanionSeeder.DemoUserId, conversationId, "what should we work on today?");
        }

        using var verify = host.CreateScope();
        var messages = await verify.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 10);

        var assistant = messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal("stop", assistant.FinishReason);      // finished naturally
        Assert.Equal(1, assistant.GenerationRounds);        // one round, no continuation
        Assert.False(assistant.Truncated);
        Assert.Equal("mock", assistant.ModelUsed);

        var user = messages.Single(m => m.Role == MessageRole.User);
        Assert.Null(user.FinishReason);                     // user messages carry no generation metadata
        Assert.Null(user.GenerationRounds);
        Assert.Null(user.ModelUsed);
    }
}
