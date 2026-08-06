using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class PersonaAndFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    [Fact]
    public async Task Persona_IsInjectedIntoTheTurnPrompt()
    {
        await using var host = new TestHost(Now);

        Guid conversationId;
        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IProfileStore>()
                .SetPersonaAsync(User, "You are a terse, dry-witted first mate.");
            var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test");
            conversationId = conv.Id;
        }

        using (var turnScope = host.CreateScope())
        {
            var companion = turnScope.ServiceProvider.GetRequiredService<ICompanion>();
            var trace = await companion.RespondAsync(User, conversationId, "hello");
            Assert.Contains("terse, dry-witted first mate", trace.Packet.Render());
        }
    }

    [Fact]
    public async Task SetPersona_RoundTrips()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var profiles = scope.ServiceProvider.GetRequiredService<IProfileStore>();

        await profiles.SetPersonaAsync(User, "concise and warm");
        Assert.Equal("concise and warm", (await profiles.GetOrCreateAsync(User)).Persona);
    }

    [Fact]
    public async Task Feedback_IsStored_AndScopedByUser()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFeedbackStore>();

        await store.AddAsync(new FeedbackRecord
        {
            UserId = User,
            ConversationId = Guid.NewGuid(),
            UserMessage = "how's the weather",
            Response = "sunny",
            Rating = FeedbackRating.Positive,
            CreatedAt = Now,
        });

        Assert.Equal(1, await store.CountAsync(User));
        Assert.Equal(0, await store.CountAsync("someone-else"));
    }
}
