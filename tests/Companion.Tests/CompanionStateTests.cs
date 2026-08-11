using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The companion's own inner state: spirits genuinely follow how conversations feel (and decay
/// toward contentment), energy follows her day, the turn's prompt carries her mood, and
/// "what's on your mind?" leads with an honest self-report. Plus the reply-shape register: a
/// short casual message asks for a short casual reply.
/// </summary>
public class CompanionStateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> NewConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    // ---- the math (pure) ----

    [Fact]
    public void Spirits_DecayTowardContentment()
    {
        // One half-life (4 days): half the distance to 0 is gone.
        var decayed = CompanionStateTracker.DecayedSpirits(0.8, Noon.AddDays(-4), Noon);
        Assert.Equal(0.4, decayed, 3);

        // Works from below too — a low stretch doesn't last forever.
        Assert.Equal(-0.2, CompanionStateTracker.DecayedSpirits(-0.4, Noon.AddDays(-4), Noon), 3);
    }

    [Theory]
    [InlineData(3, 0.25)]   // small hours: quiet
    [InlineData(9, 0.85)]   // morning: bright
    [InlineData(21, 0.45)]  // late evening: winding down
    public void Energy_FollowsHerDay(int hour, double expected)
        => Assert.Equal(expected, CompanionStateTracker.EnergyAt(hour));

    // ---- honest contagion through real turns ----

    [Fact]
    public async Task HeavyConversations_GenuinelyWeighOnHer()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var companion = sp.GetRequiredService<ICompanion>();
        var tracker = sp.GetRequiredService<ICompanionStateTracker>();
        var conv = await NewConversationAsync(sp);

        var before = (await tracker.BuildAsync(User)).Spirits;
        await companion.RespondAsync(User, conv, "I'm devastated, everything fell apart today");
        await companion.RespondAsync(User, conv, "honestly I'm so exhausted and hopeless about it");
        var after = (await tracker.BuildAsync(User)).Spirits;

        Assert.True(after < before, $"spirits should drop after heavy turns ({before} -> {after})");
    }

    [Fact]
    public async Task PrivateTurns_DoNotMoveHerSpirits()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var tracker = sp.GetRequiredService<ICompanionStateTracker>();
        var conv = await NewConversationAsync(sp);
        await conversations.SetDoNotRememberAsync(conv, User, true);

        var before = (await tracker.BuildAsync(User)).Spirits;
        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "I'm absolutely devastated about it");
        var after = (await tracker.BuildAsync(User)).Spirits;

        // The privacy gate covers her own state too: a private turn leaves no trace anywhere.
        Assert.Equal(before, after, 6);
    }

    [Fact]
    public async Task HerMood_ReachesTheTurnsPrompt_WithTheHonestyRules()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey there, how's it going?");
        var rendered = trace.Packet.Render();

        Assert.Contains("Your own mood right now", rendered);
        Assert.Contains("never imply the user caused it", rendered);
    }

    [Fact]
    public async Task WhatsOnYourMind_LeadsWithHowSheIs()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(User, conv, "what's on your mind?");

        Assert.StartsWith("Right now? I'm", reply.Text);
    }

    // ---- the reply-shape register ----

    [Fact]
    public void ShortCasualMessage_AsksForAShortReply()
    {
        var note = RegisterAdvisor.Advise("lol fair");
        Assert.NotNull(note);
        Assert.Contains("One or two sentences", note);
    }

    [Fact]
    public void ConversationalMessage_AsksForAConversationalReply()
    {
        var note = RegisterAdvisor.Advise("I've been thinking about switching jobs but I'm not sure the timing is right.");
        Assert.NotNull(note);
        Assert.Contains("conversational", note);
    }

    [Fact]
    public void ASubstantialAsk_GetsNoShapeNote()
        => Assert.Null(RegisterAdvisor.Advise(new string('x', 60) +
            " Please write me a detailed plan for the garden this spring, including what to plant in each bed, " +
            "when to start seeds indoors, and how to handle the shady corner by the fence."));

    [Fact]
    public async Task TheShapeNote_ReachesThePrompt()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "ok cool");

        Assert.Contains("Reply shape for this turn", trace.Packet.Render());
    }
}
