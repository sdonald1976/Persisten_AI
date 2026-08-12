using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Anticipation — caring at the right moments. A dated plan in the user's words becomes a held
/// event; the greeting wishes luck on/just before the day and asks how it went after; each is
/// said once and the arc closes. Detection is deliberately conservative.
/// </summary>
public class AnticipationTests
{
    // A Tuesday at noon.
    private static readonly DateTimeOffset Tuesday = new(2026, 1, 6, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    // ---- the detector (pure) ----

    [Fact]
    public void Detects_IHaveAnEventTomorrow()
    {
        var hit = AnticipationDetector.Detect("I have an interview tomorrow and I'm nervous", Tuesday);
        Assert.NotNull(hit);
        Assert.Equal("the interview", hit!.Description);
        Assert.Equal(Tuesday.Date.AddDays(1), hit.EventAt.Date);
    }

    [Fact]
    public void Detects_MyEventIsOnAWeekday_AndSaysYour()
    {
        var hit = AnticipationDetector.Detect("my friend's graduation is on Saturday!", Tuesday);
        Assert.NotNull(hit);
        Assert.Equal("your friend's graduation", hit!.Description);
        Assert.Equal(DayOfWeek.Saturday, hit.EventAt.Date.DayOfWeek);
        Assert.True(hit.EventAt.Date > Tuesday.Date);
    }

    [Fact]
    public void AWeekdayNamedOnThatWeekday_MeansNextWeek()
    {
        // Said on a Tuesday, "on Tuesday" means the NEXT one — same-day plans say "today".
        var hit = AnticipationDetector.Detect("I have a checkup on Tuesday", Tuesday);
        Assert.NotNull(hit);
        Assert.Equal(Tuesday.Date.AddDays(7), hit!.EventAt.Date);
    }

    [Theory]
    [InlineData("I had an interview yesterday")]           // past, not a plan
    [InlineData("I have a feeling tomorrow will be rough")] // not an event
    [InlineData("what should I do tomorrow?")]              // no event noun
    [InlineData("how do I center a div?")]                  // ordinary chat
    public void OrdinaryTalk_MintsNothing(string message)
        => Assert.Null(AnticipationDetector.Detect(message, Tuesday));

    // ---- capture through a real turn ----

    [Fact]
    public async Task ADatedPlanInATurn_BecomesAHeldAnticipation()
    {
        await using var host = new TestHost(Tuesday);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "by the way, I have an interview tomorrow");

        var held = Assert.Single(await sp.GetRequiredService<IAnticipationStore>().GetOpenAsync(User));
        Assert.Equal("the interview", held.Description);
        Assert.Equal(AnticipationStatus.Pending, held.Status);
    }

    [Fact]
    public async Task PrivateTurns_MintNoAnticipation()
    {
        await using var host = new TestHost(Tuesday);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conv = (await conversations.StartConversationAsync(User, "t", "mock", "test")).Id;
        await conversations.SetDoNotRememberAsync(conv, User, true);

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "I have an interview tomorrow");

        Assert.Empty(await sp.GetRequiredService<IAnticipationStore>().GetOpenAsync(User));
    }

    // ---- the greeting arc: good luck → how did it go → closed ----

    [Fact]
    public async Task TheGreeting_WishesLuck_ThenFollowsUp_EachOnce()
    {
        await using var host = new TestHost(Tuesday);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IAnticipationStore>();
        await store.AddAsync(new Anticipation
        {
            UserId = User, Description = "your interview",
            EventAt = new DateTimeOffset(Tuesday.Date.AddDays(1), Tuesday.Offset), // tomorrow
            Status = AnticipationStatus.Pending, CreatedAt = Tuesday,
        });
        var greeter = sp.GetRequiredService<IGreeter>();

        // Day before: encouragement, once.
        var eve = await greeter.GreetAsync(User);
        Assert.Contains(eve.Openers, o => o.Contains("Good luck with your interview tomorrow"));
        var again = await greeter.GreetAsync(User);
        Assert.DoesNotContain(again.Openers, o => o.Contains("Good luck"));

        // Two days later (event has passed): the follow-up — and the arc closes.
        host.Clock.Advance(TimeSpan.FromDays(2));
        var after = await greeter.GreetAsync(User);
        Assert.Contains(after.Openers, o => o.Contains("How did your interview go?"));
        Assert.Empty(await store.GetOpenAsync(User));
    }

    // ---- the sleep cycle lets long-passed events go ----

    [Fact]
    public async Task ALongPassedEvent_ExpiresInsteadOfBeingDredgedUp()
    {
        await using var host = new TestHost(Tuesday);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IAnticipationStore>();
        await store.AddAsync(new Anticipation
        {
            UserId = User, Description = "the recital",
            EventAt = new DateTimeOffset(Tuesday.Date.AddDays(-10), Tuesday.Offset),
            Status = AnticipationStatus.Pending, CreatedAt = Tuesday.AddDays(-11),
        });

        var expired = await store.ExpireStaleAsync(User, Tuesday.Date - SleepCycle.StaleAnticipationAge);

        Assert.Equal(1, expired);
        Assert.Empty(await store.GetOpenAsync(User));
    }

    [Fact]
    public async Task Anticipations_AreUserIsolated()
    {
        await using var host = new TestHost(Tuesday);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAnticipationStore>();
        await store.AddAsync(new Anticipation
        {
            UserId = User, Description = "the interview",
            EventAt = Tuesday.AddDays(1), Status = AnticipationStatus.Pending, CreatedAt = Tuesday,
        });

        Assert.Empty(await store.GetOpenAsync("someone-else"));
    }
}
