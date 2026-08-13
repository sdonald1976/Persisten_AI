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
/// Her own experiences reaching her thoughts — the point at which having a world improves what she
/// can remember rather than only where she is.
///
/// Two things are being protected here. That a day she spent somewhere is material she can reflect
/// on even when nobody spoke to her, because until now every thought she could have was derived
/// from something the user said. And that none of it ever becomes a fact about the user's real
/// life: an afternoon in the greenhouse says nothing about them.
/// </summary>
public class ExperienceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private const string MusingJson =
        """{"musing":"I spent most of the afternoon in the greenhouse. I keep going back there.","curiosities":[]}""";

    private static Reflector BuildReflector(IServiceProvider sp, string cannedResponse)
        => new(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IReflectionStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IEmotionStore>(),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IPreferenceStore>(),
            sp.GetRequiredService<IAttentionStore>(),
            sp.GetRequiredService<IMemoryAssociationStore>(),
            sp.GetRequiredService<IProcedureStore>(),
            sp.GetRequiredService<ISharedPerspectiveStore>(),
            sp.GetRequiredService<IExperienceStore>(),
            new CannedChatModel(cannedResponse),
            sp.GetRequiredService<IEmbeddingModel>(),
            sp.GetRequiredService<IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<Reflector>.Instance);

    private static async Task SpendTheAfternoonAsync(
        IServiceProvider sp, int count, DateTimeOffset? from = null)
    {
        var store = sp.GetRequiredService<IExperienceStore>();
        var at = from ?? Now.AddHours(-3);
        var places = new[] { "greenhouse", "kitchen", "hall", "study", "garden" };

        for (var i = 0; i < count; i++)
        {
            await store.AddAsync(new Experience
            {
                Id = Guid.NewGuid(),
                UserId = User,
                At = at,
                Source = "world",
                Kind = "arrived",
                Text = $"You went to the {places[i % places.Length]}.",
            });
            at = at.AddMinutes(7);
        }
    }

    // ---- the store ----

    [Fact]
    public async Task ExperiencesComeBackOldestFirst_SinceAWatermark()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 4);

        var store = sp.GetRequiredService<IExperienceStore>();
        var all = await store.GetSinceAsync(User, null, 100);

        Assert.Equal(4, all.Count);
        Assert.True(all[0].At < all[^1].At);

        var later = await store.GetSinceAsync(User, all[1].At, 100);
        Assert.Equal(2, later.Count);
    }

    [Fact]
    public async Task TheSameThingReportedTwice_IsRecordedOnce()
    {
        // A world that reconnects and replays, or sends a duplicate frame, must not make her
        // believe it happened twice.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExperienceStore>();

        Experience Arriving() => new()
        {
            Id = Guid.NewGuid(), UserId = User, At = Now, Source = "world",
            Kind = "arrived", Text = "You went to the greenhouse.",
        };

        Assert.True(await store.AddAsync(Arriving()));
        Assert.False(await store.AddAsync(Arriving()));
        Assert.Equal(1, await store.CountSinceAsync(User, null));
    }

    [Fact]
    public async Task GoingBackSomewhereLater_IsARealEvent_NotADuplicate()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExperienceStore>();

        async Task Go(string place, DateTimeOffset at) => await store.AddAsync(new Experience
        {
            Id = Guid.NewGuid(), UserId = User, At = at, Source = "world",
            Kind = "arrived", Text = $"You went to the {place}.",
        });

        await Go("greenhouse", Now);
        await Go("kitchen", Now.AddMinutes(5));
        await Go("greenhouse", Now.AddMinutes(10));

        Assert.Equal(3, await store.CountSinceAsync(User, null));
    }

    [Fact]
    public async Task OldExperiences_ArePruned()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 3, from: Now.AddDays(-60));
        await SpendTheAfternoonAsync(sp, 2, from: Now.AddHours(-1));

        var store = sp.GetRequiredService<IExperienceStore>();
        var removed = await store.PruneAsync(Now - SleepCycle.ExperienceRetention);

        Assert.Equal(3, removed);
        Assert.Equal(2, await store.CountSinceAsync(User, null));
    }

    // ---- reaching her thoughts ----

    [Fact]
    public async Task ADaySpentSomewhere_IsWorthAThought_EvenIfNobodySpoke()
    {
        // The whole point. Before this, a day with no conversation gave her nothing to think
        // about, because every thought she could have was derived from something the user said.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 8);

        var result = await BuildReflector(sp, MusingJson).ReflectResultAsync(User);

        Assert.NotNull(result);
        Assert.True(result!.Reflection.HasMusing);
        Assert.Contains("greenhouse", result.Reflection.Musing);
    }

    [Fact]
    public async Task CrossingOneRoom_IsNotADayWorthWritingAbout()
    {
        // A diary that records every doorway is noise she then reads back forever.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 2);

        Assert.Null(await BuildReflector(sp, MusingJson).ReflectResultAsync(User));
    }

    [Fact]
    public async Task TheWatermarkCoversHerOwnDay_SoItIsNotReadTwice()
    {
        // The watermark has to advance past experiences as well as messages, or a quiet day is
        // reflected on again every single pass, forever.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 8);
        var reflector = BuildReflector(sp, MusingJson);

        Assert.NotNull(await reflector.ReflectResultAsync(User));
        Assert.Null(await reflector.ReflectResultAsync(User));
    }

    [Fact]
    public async Task HerDayNeverBecomesAFactAboutTheUser()
    {
        // The rule the whole design rests on. Her afternoon says nothing about their life, and
        // nothing about it may reach the fact store.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 8);

        await BuildReflector(sp, MusingJson).ReflectResultAsync(User);

        var memories = await sp.GetRequiredService<IMemoryStore>().GetRetrievalCandidatesAsync(User);
        Assert.DoesNotContain(memories, m => m.Content.Contains("greenhouse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memories, m => m.Content.Contains("kitchen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnInventedSharedMoment_IsStillRefused_WhenOnlyHerDayHappened()
    {
        // She was somewhere; the user was not there and said nothing. A model claiming they shared
        // the moment has no evidence for it, and the existing gate must still refuse — world
        // material must not become a back door into memories about them.
        const string claimsTheyWereThere =
            """
            {"musing":"A good afternoon.","curiosities":[],
             "sharedMoments":[{"description":"We repotted the basil together","excerpt":"we repotted the basil together"}]}
            """;

        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SpendTheAfternoonAsync(sp, 8);

        var result = await BuildReflector(sp, claimsTheyWereThere).ReflectResultAsync(User);

        Assert.NotNull(result);
        Assert.Empty(result!.SharedMoments);
    }
}
