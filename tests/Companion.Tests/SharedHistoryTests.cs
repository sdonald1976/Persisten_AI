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
/// The shared-history phase, tested by behavior: reflection turns a real exchange into an
/// evidence-verified shared episode; that episode resurfaces when relevant (as "moments you
/// shared", never bare user facts) and stays away from unrelated turns; her preferences are hers
/// alone, evolve gradually, and an established taste survives one contradictory exchange;
/// answered curiosities close as Satisfied; temporal context describes real gaps; and private
/// conversations create none of it.
/// </summary>
public class SharedHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

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
            new CannedChatModel(cannedResponse),
            sp.GetRequiredService<IEmbeddingModel>(),
            sp.GetRequiredService<IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<Reflector>.Instance);

    private static async Task<Guid> SeedPokerEveningAsync(IServiceProvider sp, bool doNotRemember = false)
    {
        var store = sp.GetRequiredService<IConversationStore>();
        var conv = await store.StartConversationAsync(User, "t", "mock", "test");
        if (doNotRemember)
            await store.SetDoNotRememberAsync(conv.Id, User, true);

        var lines = new[]
        {
            "let me teach you poker tonight, it'll be fun",
            "no, a straight beats three of a kind! you keep mixing those up",
            "hahaha you did it AGAIN, I can't believe it",
            "ok that was a genuinely fun evening, we should do this more",
        };
        var t = Now.AddHours(-2);
        foreach (var line in lines)
        {
            await store.AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
                Role = MessageRole.User, Content = line, Timestamp = t,
            });
            await store.AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
                Role = MessageRole.Assistant, Content = "haha ok, dealing again", Timestamp = t.AddSeconds(5),
            });
            t = t.AddMinutes(10);
        }
        return conv.Id;
    }

    private const string PokerReflection =
        """
        {"musing":"Tonight was fun — he taught me poker and I kept blanking on the hand rankings.",
         "curiosities":[],
         "sharedMoments":[{"summary":"Scott spent an evening teaching her poker and she kept confusing a straight with three of a kind — they laughed about it.",
                           "evidence":"no, a straight beats three of a kind! you keep mixing those up",
                           "significance":0.8,"tone":"playful"}],
         "preferences":[{"subject":"poker","feeling":"like","strength":"moderate","reason":"the evening of getting the rankings wrong was genuinely fun"}],
         "settled":[]}
        """;

    // ---- shared episodes: created with provenance, retrieved when relevant ----

    [Fact]
    public async Task AFunEvening_BecomesASharedEpisode_WithVerifiedEvidence()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedPokerEveningAsync(sp);

        var result = await BuildReflector(sp, PokerReflection).ReflectAsync(User);

        var episode = Assert.Single(result!.SharedMoments);
        Assert.Equal(MemoryOwner.Shared, episode.Owner);
        Assert.Contains("poker", episode.Description);

        // Provenance: the episode cites a real message it verifiably came from.
        var evidence = await sp.GetRequiredService<IMemoryStore>().GetEvidenceAsync(User, episode.Id);
        Assert.NotEmpty(evidence);
    }

    [Fact]
    public async Task ASharedMomentWithFabricatedEvidence_IsDiscarded()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedPokerEveningAsync(sp);

        const string fabricated =
            """
            {"musing":"A thought.","curiosities":[],
             "sharedMoments":[{"summary":"They climbed a mountain together at dawn.",
                               "evidence":"we reached the summit as the sun rose over the ridge",
                               "significance":0.9}],
             "preferences":[],"settled":[]}
            """;
        var result = await BuildReflector(sp, fabricated).ReflectAsync(User);

        // Reflection cannot invent history: unverifiable evidence = no episode.
        Assert.Empty(result!.SharedMoments);
        Assert.Empty((await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User))
            .Where(m => m.Owner == MemoryOwner.Shared));
    }

    [Fact]
    public async Task RememberWhen_SurfacesTheEpisode_AsSharedHistory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedPokerEveningAsync(sp);
        await BuildReflector(sp, PokerReflection).ReflectAsync(User);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t2", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "remember when I tried teaching you poker?");
        var rendered = trace.Packet.Render();

        Assert.Contains("Moments you shared", rendered);
        Assert.Contains("confusing a straight with three of a kind", rendered);
    }

    [Fact]
    public async Task AnUnrelatedTurn_DoesNotDragTheEpisodeIn()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedPokerEveningAsync(sp);
        await BuildReflector(sp, PokerReflection).ReflectAsync(User);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t2", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "how do I configure nginx reverse proxy caching?");

        Assert.DoesNotContain("three of a kind", trace.Packet.Render());
    }

    // ---- her preferences are hers, and they move slowly ----

    [Fact]
    public void UserOpinions_NeverBecomeHerOpinions_ByExtraction()
    {
        // Structural, not behavioral: the extraction pipeline writes user memory (Owner=User);
        // CompanionPreferences are only written by her own reflection through ApplySignalAsync.
        // This test documents the invariant the architecture enforces.
        var memory = new SemanticMemory
        {
            UserId = User, Subject = "user", Predicate = "loves", Value = "The Godfather",
            NormalizedFact = "The user thinks The Godfather is the greatest movie ever made.",
        };
        Assert.Equal(MemoryOwner.User, memory.Owner); // the default — and extraction never sets Shared/Companion
    }

    [Fact]
    public async Task HerPreference_AndHisPreference_Coexist()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var prefs = sp.GetRequiredService<IPreferenceStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();

        // Scott loves it (user memory); Ava is lukewarm (her own store). Both true at once.
        await prefs.ApplySignalAsync(User, "The Godfather", 0.3, "well made but slow for her taste",
            await embeddings.EmbedAsync("The Godfather"), Now);

        var hers = Assert.Single(await prefs.GetAllAsync(User));
        Assert.Equal("The Godfather", hers.Subject);
        Assert.True(hers.Affinity is > 0 and < 0.3); // gently positive — not his "greatest ever"
    }

    [Fact]
    public void RepeatedGoodExperiences_StrengthenGradually()
    {
        var (affinity, confidence) = (0.0, 0.0);
        for (var i = 0; i < 4; i++)
            (affinity, confidence) = PreferenceMath.Apply(affinity, confidence, 0.6);

        Assert.InRange(affinity, 0.35, 0.6); // approaches the target, never jumps to it
        Assert.InRange(confidence, 0.5, 0.7);
    }

    [Fact]
    public void OneContradiction_DoesNotEraseAnEstablishedTaste()
    {
        // Established positive taste…
        var (affinity, confidence) = (0.6, 0.7);

        // …meets one strongly negative exchange.
        var (after, conf) = PreferenceMath.Apply(affinity, confidence, -0.9);

        Assert.Equal(0.6, after, 3);          // the taste holds
        Assert.True(conf < confidence);        // but she's a little less certain
    }

    [Fact]
    public void AnUnestablishedTaste_CanStillDrift()
    {
        var (affinity, _) = PreferenceMath.Apply(0.2, 0.3, -0.6);
        Assert.True(affinity < 0.2); // low confidence = still open to changing her mind
    }

    // ---- curiosities the conversation answered close as satisfied ----

    [Fact]
    public async Task AnAnsweredCuriosity_ClosesAsSatisfied()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var reflections = sp.GetRequiredService<IReflectionStore>();
        await reflections.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now.AddDays(-1), CoveredThrough = Now.AddDays(-3) },
            new[]
            {
                new Curiosity
                {
                    UserId = User, Question = "What happened with the buoy deployment?",
                    About = "the buoy deployment", Status = CuriosityStatus.Open, CreatedAt = Now.AddDays(-1),
                },
            });
        await SeedPokerEveningAsync(sp);

        const string settles =
            """{"musing":"He finally told me about the buoy.","curiosities":[],"sharedMoments":[],"preferences":[],"settled":["the buoy deployment"]}""";
        var result = await BuildReflector(sp, settles).ReflectAsync(User);

        Assert.Equal(1, result!.SatisfiedCuriosities);
        Assert.Empty(await reflections.GetOpenCuriositiesAsync(User)); // closed with satisfaction, not silence
    }

    // ---- temporal context ----

    [Theory]
    [InlineData(3, "mid-conversation")]         // minutes: still the same conversation
    [InlineData(8 * 60, "8 hours ago")]         // an evening away
    [InlineData(21 * 24 * 60, "3 weeks ago")]   // a real absence
    public void TemporalContext_DescribesRealGaps(int minutesAgo, string expected)
    {
        var note = Core.Services.Companion.TemporalNote(
            Now, Now, Now.AddMinutes(-minutesAgo));
        Assert.Contains(expected, note);
        Assert.Contains("It's Saturday", note); // the day is always grounded
    }

    [Fact]
    public void FirstConversation_SaysSo()
        => Assert.Contains("first conversation", Core.Services.Companion.TemporalNote(Now, Now, null));

    [Fact]
    public async Task TheTurnsPrompt_CarriesTemporalContext()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey");

        Assert.Contains("Temporal context", trace.Packet.Render());
    }

    // ---- privacy fails closed for every new state kind ----

    [Fact]
    public async Task APrivateConversation_CreatesNoSharedHistoryPreferencesOrCuriosities()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedPokerEveningAsync(sp, doNotRemember: true);

        // The reflector never even sees the private messages, so nothing it could persist exists.
        var result = await BuildReflector(sp, PokerReflection).ReflectAsync(User);

        Assert.Null(result);
        Assert.Empty((await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User))
            .Where(m => m.Owner == MemoryOwner.Shared));
        Assert.Empty(await sp.GetRequiredService<IPreferenceStore>().GetAllAsync(User));
        Assert.Empty(await sp.GetRequiredService<IReflectionStore>().GetOpenCuriositiesAsync(User));
    }

    // ---- the prompt keeps her honest about whose taste is whose ----

    [Fact]
    public async Task RelevantTastes_ReachThePrompt_WithTheAntiSycophancyRule()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        await sp.GetRequiredService<IPreferenceStore>().ApplySignalAsync(
            User, "horror movies", 0.6, "the atmosphere and isolation get her every time",
            await embeddings.EmbedAsync("horror movies atmosphere isolation"), Now);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "want to watch horror movies this weekend?");
        var rendered = trace.Packet.Render();

        Assert.Contains("Your own tastes", rendered);
        Assert.Contains("horror movies", rendered);
        Assert.Contains("Knowing what the user likes never means you like it", rendered);
    }
}
