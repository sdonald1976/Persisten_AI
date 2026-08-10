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
/// The between-session reflection pass (the inner monologue): it only thinks when there is enough
/// new material, never reads private conversations, writes a musing + curiosities from the model's
/// JSON, advances its watermark on quiet days, and stores nothing when the model output is junk.
/// </summary>
public class ReflectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private const string MusingJson =
        """{"musing":"She mentioned her sister twice now, but I still don't know her name. The interview seems to weigh on her.","curiosities":[{"question":"What's your sister's name?","about":"her sister","reason":"mentioned twice, never named"}]}""";

    private const string QuietJson = """{"musing":null,"curiosities":[]}""";

    private static Reflector BuildReflector(IServiceProvider sp, string cannedResponse)
        => new(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IReflectionStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IEmotionStore>(),
            new CannedChatModel(cannedResponse),
            sp.GetRequiredService<IEmbeddingModel>(),
            sp.GetRequiredService<IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<Reflector>.Instance);

    /// <summary>Adds <paramref name="userMessages"/> user turns (each with a reply) to a fresh conversation.</summary>
    private static async Task<Guid> SeedConversationAsync(
        IServiceProvider sp, int userMessages, bool doNotRemember = false, DateTimeOffset? startAt = null)
    {
        var store = sp.GetRequiredService<IConversationStore>();
        var conv = await store.StartConversationAsync(User, "t", "mock", "test");
        if (doNotRemember)
            await store.SetDoNotRememberAsync(conv.Id, User, true);

        var t = startAt ?? Now.AddMinutes(-userMessages);
        for (var i = 0; i < userMessages; i++)
        {
            await store.AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
                Role = MessageRole.User, Content = $"my sister called me again today ({i})", Timestamp = t,
            });
            await store.AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
                Role = MessageRole.Assistant, Content = "that's lovely", Timestamp = t.AddSeconds(10),
            });
            t = t.AddMinutes(1);
        }
        return conv.Id;
    }

    [Fact]
    public async Task TooLittleNewMaterial_ProducesNoReflection()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 2); // below the default minimum of 3

        var result = await BuildReflector(sp, MusingJson).ReflectAsync(User);

        Assert.Null(result);
        Assert.Null(await sp.GetRequiredService<IReflectionStore>().GetLatestAsync(User));
    }

    [Fact]
    public async Task EnoughMaterial_WritesAMusing_AndMintsCuriosities()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);

        var result = await BuildReflector(sp, MusingJson).ReflectAsync(User);

        Assert.NotNull(result);
        Assert.True(result!.Reflection.HasMusing);
        Assert.Contains("sister", result.Reflection.Musing);
        Assert.NotNull(result.Reflection.Embedding); // a thought can be found again later

        var curiosity = Assert.Single(result.Curiosities);
        Assert.Equal("What's your sister's name?", curiosity.Question);
        Assert.Equal(CuriosityStatus.Open, curiosity.Status);

        var open = await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User);
        Assert.Single(open);
    }

    [Fact]
    public async Task PrivateConversations_NeverReachTheReflectionPass()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 6, doNotRemember: true);

        var result = await BuildReflector(sp, MusingJson).ReflectAsync(User);

        // Plenty of messages exist — but none are rememberable, so the companion never thinks
        // about them: no musing, no watermark, nothing.
        Assert.Null(result);
        Assert.Null(await sp.GetRequiredService<IReflectionStore>().GetLatestAsync(User));
    }

    [Fact]
    public async Task TheWatermark_PreventsThinkingAboutTheSameTurnsTwice()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);
        var reflector = BuildReflector(sp, MusingJson);

        Assert.NotNull(await reflector.ReflectAsync(User));

        // Nothing new since the pass — the same material must not produce a second thought.
        Assert.Null(await reflector.ReflectAsync(User));
    }

    [Fact]
    public async Task AQuietDay_AdvancesTheWatermark_WithoutAMusing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);
        var reflector = BuildReflector(sp, QuietJson);

        var result = await reflector.ReflectAsync(User);

        Assert.NotNull(result);
        Assert.False(result!.Reflection.HasMusing);
        Assert.Empty(result.Curiosities);

        // The quiet pass still advanced the watermark: the material is not re-read.
        Assert.Null(await reflector.ReflectAsync(User));
    }

    [Fact]
    public async Task UnusableModelOutput_StoresNothing_SoTheMaterialIsRetried()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);

        Assert.Null(await BuildReflector(sp, "I had a lovely think about her sister.").ReflectAsync(User));
        Assert.Null(await sp.GetRequiredService<IReflectionStore>().GetLatestAsync(User));

        // A model failure is not a quiet day: the watermark did not move, so a later pass with a
        // working model still sees the material and the thought is not lost.
        Assert.NotNull(await BuildReflector(sp, MusingJson).ReflectAsync(User));
    }

    [Fact]
    public async Task ACuriosityAlreadyHeld_IsNotMintedTwice()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);
        var reflector = BuildReflector(sp, MusingJson);

        Assert.NotNull(await reflector.ReflectAsync(User));

        // More conversation (after the watermark), and the model proposes the SAME wondering again.
        await SeedConversationAsync(sp, userMessages: 4, startAt: Now.AddMinutes(10));
        var second = await reflector.ReflectAsync(User);

        Assert.NotNull(second);
        Assert.Empty(second!.Curiosities); // deduped against what's already held

        var open = await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User);
        Assert.Single(open);
    }

    [Fact]
    public async Task CuriositiesPerPass_AreCapped()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedConversationAsync(sp, userMessages: 4);

        const string threeCuriosities =
            """{"musing":"So much I wonder about.","curiosities":[{"question":"A?","about":"a"},{"question":"B?","about":"b"},{"question":"C?","about":"c"}]}""";
        var result = await BuildReflector(sp, threeCuriosities).ReflectAsync(User);

        // Wonder about a couple of things, not everything (default cap is 2).
        Assert.Equal(2, result!.Curiosities.Count);
    }

    [Fact]
    public async Task Curiosities_AreUserIsolated()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IReflectionStore>();

        await store.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now, CoveredThrough = Now },
            new[] { new Curiosity { UserId = User, Question = "Q?", Status = CuriosityStatus.Open, CreatedAt = Now } });

        Assert.Empty(await store.GetOpenCuriositiesAsync("someone-else"));
        Assert.Null(await store.GetNextToVoiceAsync("someone-else", Now, TimeSpan.FromHours(1)));
    }
}
