using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// End-to-end through the real turn pipeline: an emotionally-loaded message is captured as a signal,
/// the derived tone reaches the next turn's context packet, privacy turns capture nothing, and the
/// greeter opens with care after a low stretch.
/// </summary>
public class RelationalMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> NewConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    [Fact]
    public async Task AnEmotionalMessage_IsCapturedAsASignal()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "honestly I've been really stressed about the deadline");

        var signals = await sp.GetRequiredService<IEmotionStore>().GetRecentSignalsAsync(User, 10);
        var signal = Assert.Single(signals);
        Assert.Equal(Sentiment.VeryNegative, signal.Sentiment);
        Assert.Equal("stressed", signal.Label);
    }

    [Fact]
    public async Task FlatMessage_CapturesNoSignal()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await NewConversationAsync(sp);

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "what time does the library open?");

        Assert.Empty(await sp.GetRequiredService<IEmotionStore>().GetRecentSignalsAsync(User, 10));
    }

    [Fact]
    public async Task RecentMood_ReachesTheNextTurnsContextPacket()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var companion = sp.GetRequiredService<ICompanion>();
        var conv = await NewConversationAsync(sp);

        // A couple of low turns build the tone…
        await companion.RespondAsync(User, conv, "I'm feeling really overwhelmed lately");
        await companion.RespondAsync(User, conv, "everything has been so exhausting, I'm drained");

        // …which the next turn's packet reflects as tone guidance (not a stated fact).
        var trace = await companion.RespondAsync(User, conv, "anyway, how do I center a div?");
        var rendered = trace.Packet.Render();

        Assert.Contains("How things have been", rendered);
        Assert.Contains("gentler", rendered);
    }

    [Fact]
    public async Task PrivateConversation_CapturesNoMood()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conv = await NewConversationAsync(sp);
        await conversations.SetDoNotRememberAsync(conv, User, true);

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "I'm absolutely devastated about it");

        Assert.Empty(await sp.GetRequiredService<IEmotionStore>().GetRecentSignalsAsync(User, 10));
    }

    [Fact]
    public async Task Greeter_OpensWithCare_AfterALowStretch()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var emotions = sp.GetRequiredService<IEmotionStore>();

        // Seed a low recent history directly.
        foreach (var (min, label, val) in new[] { (0, "worried", -0.6), (1, "stressed", -0.6), (2, "overwhelmed", -0.65) })
            await emotions.AddSignalAsync(new EmotionalSignal
            {
                Id = Guid.NewGuid(),
                UserId = User,
                MessageId = Guid.NewGuid(),
                Timestamp = Now.AddMinutes(min),
                Sentiment = Sentiment.Negative,
                Valence = val,
                Label = label,
            });

        var greeting = await sp.GetRequiredService<IGreeter>().GreetAsync(User);

        Assert.Contains(greeting.Openers, o =>
            o.Contains("talk about it", StringComparison.OrdinalIgnoreCase));
    }
}
