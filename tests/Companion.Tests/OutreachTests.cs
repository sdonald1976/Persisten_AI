using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The companion reaching out on her own — and, more importantly, all the reasons she doesn't:
/// never to someone she hasn't met, never while they're around, never over budget, never at 3am,
/// and never without something real to say. Sending spends the curiosity (voiced-on-send) and a
/// failed delivery burns nothing.
/// </summary>
public class OutreachTests
{
    // Noon UTC; quiet hours in these tests use a UTC-local clock (see UtcTimeProvider below).
    private static readonly DateTimeOffset Noon = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private sealed class RecordingChannel : IOutboundChannel
    {
        public bool Configured { get; set; } = true;
        public bool FailNext { get; set; }
        public List<(string Title, string Body)> Sent { get; } = new();

        public Task<bool> SendAsync(string title, string body, CancellationToken ct = default)
        {
            if (FailNext) { FailNext = false; return Task.FromResult(false); }
            Sent.Add((title, body));
            return Task.FromResult(true);
        }
    }

    /// <summary>A fixed clock whose LOCAL time equals its UTC time, so quiet-hour tests don't
    /// depend on the machine's timezone.</summary>
    private sealed class UtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public UtcTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static OutreachService Build(
        IServiceProvider sp, RecordingChannel channel, DateTimeOffset now, Action<OutreachOptions>? tune = null)
    {
        var options = new OutreachOptions { NtfyUrl = "https://ntfy.example/topic" };
        tune?.Invoke(options);
        return new OutreachService(
            channel,
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IOutreachStore>(),
            sp.GetRequiredService<IReflectionStore>(),
            sp.GetRequiredService<IAnticipationStore>(),
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IPersonalityService>(),
            Options.Create(options),
            new UtcTimeProvider(now),
            NullLogger<OutreachService>.Instance);
    }

    /// <summary>One conversation with one exchange at <paramref name="at"/> — "the user was here then".</summary>
    private static async Task SeenAtAsync(IServiceProvider sp, DateTimeOffset at)
    {
        var store = sp.GetRequiredService<IConversationStore>();
        var conv = await store.StartConversationAsync(User, "t", "mock", "test");
        await store.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
            Role = MessageRole.User, Content = "hi", Timestamp = at,
        });
    }

    private static async Task HoldsCuriosityAsync(IServiceProvider sp, string question, DateTimeOffset at)
        => await sp.GetRequiredService<IReflectionStore>().AddAsync(
            new Reflection { UserId = User, CreatedAt = at, CoveredThrough = at },
            new[] { new Curiosity { UserId = User, Question = question, Status = CuriosityStatus.Open, CreatedAt = at } });

    private static async Task HoldsAnticipationAsync(IServiceProvider sp, string description, DateTimeOffset eventAt)
        => await sp.GetRequiredService<IAnticipationStore>().AddAsync(new Anticipation
        {
            UserId = User, Description = description, EventAt = eventAt,
            Status = AnticipationStatus.Pending, CreatedAt = eventAt.AddDays(-1),
        });

    [Fact]
    public async Task AwayWithACuriosity_SendsOnce_SpendsIt_AndLogsIt()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-2));
        await HoldsCuriosityAsync(sp, "How did the interview go?", Noon.AddDays(-1));
        var channel = new RecordingChannel();

        var sent = await Build(sp, channel, Noon).RunOnceAsync(User);

        Assert.NotNull(sent);
        var push = Assert.Single(channel.Sent);
        Assert.Contains("How did the interview go?", push.Body);

        // Voiced-on-send: she won't ask this again anywhere.
        Assert.Empty(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));

        // And the log remembers it happened (provenance included).
        var logged = Assert.Single(await sp.GetRequiredService<IOutreachStore>().GetRecentAsync(User, 10));
        Assert.StartsWith("curiosity:", logged.Source);
    }

    [Fact]
    public async Task SomeoneSheHasNeverMet_IsNeverMessaged()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await HoldsCuriosityAsync(sp, "Q?", Noon.AddDays(-1)); // a curiosity, but no conversation ever
        var channel = new RecordingChannel();

        Assert.Null(await Build(sp, channel, Noon).RunOnceAsync(User));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task WhileTheUserIsAround_SheStaysQuiet()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddHours(-2)); // seen two hours ago — not "away"
        await HoldsCuriosityAsync(sp, "Q?", Noon.AddHours(-1));
        var channel = new RecordingChannel();

        Assert.Null(await Build(sp, channel, Noon).RunOnceAsync(User));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task WithNothingRealToSay_NoMessageIsSent()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-2)); // away, but no curiosity held
        var channel = new RecordingChannel();

        Assert.Null(await Build(sp, channel, Noon).RunOnceAsync(User));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task TheBudget_PreventsBackToBackMessages()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-3));
        await HoldsCuriosityAsync(sp, "First?", Noon.AddDays(-1));
        await HoldsCuriosityAsync(sp, "Second?", Noon.AddDays(-1).AddMinutes(1));
        var channel = new RecordingChannel();
        var service = Build(sp, channel, Noon);

        Assert.NotNull(await service.RunOnceAsync(User));  // first goes out
        Assert.Null(await service.RunOnceAsync(User));     // second is inside the budget window
        Assert.Single(channel.Sent);
        Assert.Single(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
    }

    [Fact]
    public async Task QuietHours_HoldTheMessage()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var night = new DateTimeOffset(2026, 1, 10, 23, 30, 0, TimeSpan.Zero); // 23:30 "local"
        await SeenAtAsync(sp, night.AddDays(-2));
        await HoldsCuriosityAsync(sp, "Q?", night.AddDays(-1));
        var channel = new RecordingChannel();

        Assert.Null(await Build(sp, channel, night).RunOnceAsync(User));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task AFailedDelivery_BurnsNothing()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-2));
        await HoldsCuriosityAsync(sp, "Q?", Noon.AddDays(-1));
        var channel = new RecordingChannel { FailNext = true };
        var service = Build(sp, channel, Noon);

        Assert.Null(await service.RunOnceAsync(User));

        // The curiosity is still open and no budget was consumed — the next check just retries.
        Assert.Single(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
        Assert.Empty(await sp.GetRequiredService<IOutreachStore>().GetRecentAsync(User, 10));
        Assert.NotNull(await service.RunOnceAsync(User)); // works once delivery recovers
    }

    [Fact]
    public async Task UnconfiguredChannel_MeansTheFeatureIsSimplyOff()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-2));
        await HoldsCuriosityAsync(sp, "Q?", Noon.AddDays(-1));
        var channel = new RecordingChannel { Configured = false };

        Assert.Null(await Build(sp, channel, Noon).RunOnceAsync(User));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task EventDayEncouragement_ArrivesEvenIfTheyChattedLastNight()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddHours(-14)); // chatted last night — NOT "away"
        await HoldsAnticipationAsync(sp, "your interview", new DateTimeOffset(Noon.Date, TimeSpan.Zero));
        var channel = new RecordingChannel();

        var sent = await Build(sp, channel, Noon).RunOnceAsync(User);

        // "Good luck today" must land on the morning — the away-gate deliberately doesn't apply.
        Assert.NotNull(sent);
        Assert.Contains("Good luck with your interview today", Assert.Single(channel.Sent).Body);

        // Encouraged once; a later check has nothing new to say about it.
        Assert.Null(await Build(sp, channel, Noon.AddHours(3), o => o.MinHoursBetween = 1).RunOnceAsync(User));
    }

    [Fact]
    public async Task APassedEvent_GetsTheFollowUp_BeforeAnyCuriosity()
    {
        await using var host = new TestHost(Noon);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeenAtAsync(sp, Noon.AddDays(-2));
        await HoldsAnticipationAsync(sp, "your interview", new DateTimeOffset(Noon.Date.AddDays(-1), TimeSpan.Zero));
        await HoldsCuriosityAsync(sp, "Unrelated wondering?", Noon.AddDays(-1));
        var channel = new RecordingChannel();

        var sent = await Build(sp, channel, Noon).RunOnceAsync(User);

        Assert.NotNull(sent);
        Assert.Contains("how did your interview go?", Assert.Single(channel.Sent).Body);

        // The arc closed; the curiosity was saved for another day.
        Assert.Empty(await sp.GetRequiredService<IAnticipationStore>().GetOpenAsync(User));
        Assert.Single(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
    }

    [Theory]
    [InlineData(23, 22, 8, true)]   // late evening inside the window
    [InlineData(3, 22, 8, true)]    // small hours inside the window (crosses midnight)
    [InlineData(12, 22, 8, false)]  // midday outside
    [InlineData(9, 9, 17, true)]    // window not crossing midnight
    [InlineData(8, 9, 17, false)]
    [InlineData(5, 6, 6, false)]    // equal start/end = quiet hours disabled
    public void QuietHourMath_HandlesMidnightAndDisabled(int hour, int start, int end, bool quiet)
        => Assert.Equal(quiet, OutreachService.IsQuietHour(hour, start, end));
}
