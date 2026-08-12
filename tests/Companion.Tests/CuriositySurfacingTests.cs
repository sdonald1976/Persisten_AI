using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// How the inner monologue reaches the user: a fresh musing colors the turn's context under a
/// private "hold loosely" label, a held curiosity is offered at most once (voiced-on-offer, with a
/// cooldown so consecutive turns never each ask), and the greeter can voice one as an opener.
/// </summary>
public class CuriositySurfacingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> NewConversationAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    private static async Task SeedThoughtAsync(
        IServiceProvider sp, string? musing, DateTimeOffset createdAt, params string[] questions)
    {
        await sp.GetRequiredService<IReflectionStore>().AddAsync(
            new Reflection { UserId = User, CreatedAt = createdAt, CoveredThrough = createdAt, Musing = musing },
            questions.Select(q => new Curiosity
            {
                UserId = User, Question = q, About = q, Status = CuriosityStatus.Open, CreatedAt = createdAt,
            }).ToList());
    }

    [Fact]
    public async Task AFreshMusing_ColorsTheTurn_UnderThePrivateLabel()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, "I keep thinking about how tired she sounded.", Now.AddHours(-8));
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey, good morning");
        var rendered = trace.Packet.Render();

        Assert.Contains("A thought you had while they were away", rendered);
        Assert.Contains("how tired she sounded", rendered);
        Assert.Contains("Hold it loosely", rendered); // a thought, never a fact
    }

    [Fact]
    public async Task AStaleMusing_StopsShapingTurns()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, "An old thought from another week.", Now.AddDays(-8));
        var conv = await NewConversationAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey");

        Assert.DoesNotContain("An old thought", trace.Packet.Render());
    }

    [Fact]
    public async Task ACuriosity_IsOfferedToTheTurn_AndSpentByTheOffer()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, null, Now.AddHours(-8), "What's your project partner's name?");
        var conv = await NewConversationAsync(sp);
        var companion = sp.GetRequiredService<ICompanion>();

        var trace = await companion.RespondAsync(User, conv, "hey there");
        var rendered = trace.Packet.Render();

        Assert.Contains("Something you've been genuinely curious about", rendered);
        Assert.Contains("What's your project partner's name?", rendered);

        // Offered once = spent, whether or not the model chose to raise it.
        Assert.Empty(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
    }

    [Fact]
    public async Task ConsecutiveTurns_DoNotEachAskSomethingNew()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, null, Now.AddHours(-8), "First wondering?", "Second wondering?");
        var conv = await NewConversationAsync(sp);
        var companion = sp.GetRequiredService<ICompanion>();

        var first = await companion.RespondAsync(User, conv, "hey");
        var second = await companion.RespondAsync(User, conv, "and another thing");

        // Turn one got a curiosity; turn two is inside the cooldown, so the other stays held.
        Assert.Contains("Something you've been genuinely curious about", first.Packet.Render());
        Assert.DoesNotContain("Something you've been genuinely curious about", second.Packet.Render());
        Assert.Single(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
    }

    [Fact]
    public async Task TheGreeter_VoicesACuriosity_AsAnOpener_Once()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, null, Now.AddHours(-8), "How did the interview go?");
        var greeter = sp.GetRequiredService<IGreeter>();

        var greeting = await greeter.GreetAsync(User);
        var opener = Assert.Single(greeting.Openers, o => o.Contains("found myself wondering"));
        Assert.Contains("How did the interview go?", opener);

        // Raised once is the whole budget — the next greeting holds no repeat.
        var again = await greeter.GreetAsync(User);
        Assert.DoesNotContain(again.Openers, o => o.Contains("found myself wondering"));
    }

    [Fact]
    public async Task PrivateTurns_StillSeeThoughts_ButNothingLeaksIntoThem()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedThoughtAsync(sp, "A held thought.", Now.AddHours(-8));
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conv = await NewConversationAsync(sp);
        await conversations.SetDoNotRememberAsync(conv, User, true);

        // A private turn may still be colored by an existing musing (reading is harmless)…
        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "I'm devastated about it");
        Assert.Contains("A held thought.", trace.Packet.Render());

        // …but the private exchange itself can never become material for a later thought.
        var rememberable = await conversations.GetRememberableMessagesSinceAsync(User, null, 100);
        Assert.Empty(rememberable);
    }
}
