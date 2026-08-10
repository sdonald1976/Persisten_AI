using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// "What's on your mind?" — the inner monologue made visible on demand. The answer comes from the
/// real reflection diary (thoughts that actually happened, timestamped), a shared curiosity is
/// spent by the sharing, and asking when nothing has settled gets an honest "not yet".
/// </summary>
public class ShareThoughtsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> NewConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    [Fact]
    public async Task WithNoThoughtsYet_AnswersHonestly()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(User, conv, "what's on your mind?");

        Assert.Equal(IntentKind.ShareThoughts, reply.Intent);
        Assert.Contains("nothing's had time to settle", reply.Text);
    }

    [Fact]
    public async Task SharesTheLatestMusing_WithItsAge()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IReflectionStore>().AddAsync(
            new Reflection
            {
                UserId = User, CreatedAt = Now.AddDays(-2), CoveredThrough = Now.AddDays(-2),
                Musing = "She keeps circling back to the garden — it seems to steady her.",
            },
            Array.Empty<Curiosity>());
        var conv = await NewConversationAsync(sp);

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(User, conv, "what are you thinking about?");

        Assert.Equal(IntentKind.ShareThoughts, reply.Intent);
        Assert.Contains("While you were away (2 days ago)", reply.Text);
        Assert.Contains("circling back to the garden", reply.Text);
    }

    [Fact]
    public async Task SharingACuriosity_SpendsIt_EvenInsideTheCooldown()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var reflections = sp.GetRequiredService<IReflectionStore>();
        await reflections.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now.AddHours(-3), CoveredThrough = Now.AddHours(-3) },
            new[]
            {
                new Curiosity
                {
                    UserId = User, Question = "What's your sister's name?", About = "her sister",
                    Status = CuriosityStatus.Open, CreatedAt = Now.AddHours(-3),
                },
            });
        var conv = await NewConversationAsync(sp);
        var agent = sp.GetRequiredService<IAgent>();

        // A curiosity was just voiced elsewhere — the turn-level cooldown would hold this one back,
        // but a direct "what's on your mind?" is an invitation, so it's shared anyway.
        await reflections.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now.AddMinutes(-10), CoveredThrough = Now.AddMinutes(-10) },
            new[]
            {
                new Curiosity
                {
                    UserId = User, Question = "Already asked?", Status = CuriosityStatus.Voiced,
                    CreatedAt = Now.AddMinutes(-10), VoicedAt = Now.AddMinutes(-10),
                },
            });

        var reply = await agent.HandleAsync(User, conv, "penny for your thoughts");

        Assert.Contains("What's your sister's name?", reply.Text);
        Assert.Empty(await reflections.GetOpenCuriositiesAsync(User)); // spent by the sharing
    }

    [Fact]
    public async Task AnOpinionQuestion_IsNotHijacked()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        // "what do you think about X" asks for an opinion — an ordinary chat turn, not the diary.
        var reply = await sp.GetRequiredService<IAgent>()
            .HandleAsync(User, conv, "what do you think about my garden plan?");

        Assert.Equal(IntentKind.Chat, reply.Intent);
    }
}
