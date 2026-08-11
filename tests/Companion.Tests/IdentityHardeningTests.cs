using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

public class IdentityHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task SetDisplayNameAsync(IServiceProvider sp, string name)
    {
        var profiles = sp.GetRequiredService<IProfileStore>();
        var profile = await profiles.GetOrCreateAsync(User);
        profile.DisplayName = name;
        var db = sp.GetRequiredService<CompanionDbContext>();
        db.Users.Update(profile);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> StartAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    private static async Task AddSemanticAsync(
        IServiceProvider sp, string text, string predicate, string value,
        MemoryOwner owner = MemoryOwner.User, Validity validity = Validity.Current)
    {
        await sp.GetRequiredService<IMemoryStore>().AddSemanticAsync(new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = owner == MemoryOwner.Companion ? "companion" : "user",
            Predicate = predicate,
            Value = value,
            NormalizedFact = text,
            Owner = owner,
            Validity = validity,
            Status = MemoryStatus.Active,
            Origin = MemoryOrigin.Stated,
            Confidence = 0.95,
            Importance = 0.8,
            FirstObserved = Now.AddDays(-1),
            LastConfirmed = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1),
            Embedding = await sp.GetRequiredService<IEmbeddingModel>().EmbedAsync(text),
        });
    }

    private static Reflector BuildReflector(IServiceProvider sp, string cannedResponse)
        => new(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<IReflectionStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IEmotionStore>(),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IPreferenceStore>(),
            new CannedChatModel(cannedResponse),
            sp.GetRequiredService<IEmbeddingModel>(),
            sp.GetRequiredService<IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<Reflector>.Instance);

    [Fact]
    public async Task Prompt_ProjectsAuthoritativeIdentities_AndPronounRules()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SetDisplayNameAsync(sp, "Scott");
        var conv = await StartAsync(sp);

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey Ava");
        var rendered = trace.Packet.Render();

        Assert.Contains("# AUTHORITATIVE IDENTITIES", rendered);
        Assert.Contains("Name: Ava", rendered);
        Assert.Contains("Name: Scott", rendered);
        Assert.Contains("I / me / my refers to Ava.", rendered);
        Assert.Contains("you / your refers to Scott.", rendered);
        Assert.Contains("Never transfer identity, preferences, memories, experiences, or relationships between Ava and Scott.", rendered);
    }

    [Fact]
    public async Task PersonaRelationship_IsNormalizedIntoParticipantFacts()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SetDisplayNameAsync(sp, "Scott");
        await sp.GetRequiredService<IProfileStore>().SetPersonaAsync(User, "You are my sister.");
        var conv = await StartAsync(sp);

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "hi sis")).Packet.Render();

        Assert.Contains("# AUTHORITATIVE RELATIONSHIP", rendered);
        Assert.Contains("Ava is Scott's sister.", rendered);
        Assert.Contains("Scott is Ava's brother.", rendered);
        Assert.Contains("# AVA PERSONALITY / STYLE", rendered);
    }

    [Fact]
    public async Task RecentConversation_UsesNamedParticipantLabels()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SetDisplayNameAsync(sp, "Scott");
        var conv = await StartAsync(sp);
        var conversations = sp.GetRequiredService<IConversationStore>();
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.User, Content = "previous user line", Timestamp = Now.AddMinutes(-5),
        });
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.Assistant, Content = "previous Ava line", Timestamp = Now.AddMinutes(-4),
        });

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "continue")).Packet.Render();

        Assert.Contains("[USER - Scott]", rendered);
        Assert.Contains("[COMPANION - Ava]", rendered);
    }

    [Fact]
    public async Task Memories_RenderWithExplicitOwnersAndNames()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SetDisplayNameAsync(sp, "Scott");
        await AddSemanticAsync(sp, "The user prefers ribeye medium rare.", "prefers", "ribeye medium rare");
        await AddSemanticAsync(sp, "The companion dislikes stale coffee.", "dislikes", "stale coffee", MemoryOwner.Companion);
        await sp.GetRequiredService<IMemoryStore>().AddEpisodicAsync(new EpisodicMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Description = "spent the evening debugging the continuation system",
            Owner = MemoryOwner.Shared,
            EventTime = Now.AddDays(-1),
            MentionedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1),
            Status = MemoryStatus.Active,
            EpisodeStatus = EpisodeStatus.Occurred,
            Confidence = 0.9,
            Importance = 0.9,
            Embedding = await sp.GetRequiredService<IEmbeddingModel>().EmbedAsync("debugging continuation system"),
        });
        var conv = await StartAsync(sp);

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "ribeye coffee continuation system")).Packet.Render();

        Assert.Contains("USER MEMORY - Scott: Scott prefers ribeye medium rare.", rendered);
        Assert.Contains("COMPANION MEMORY - Ava: Ava dislikes stale coffee.", rendered);
        Assert.Contains("SHARED EXPERIENCE - Scott + Ava: Scott and Ava:", rendered);
    }

    [Fact]
    public async Task StaleIdentityMemory_DoesNotCompeteWithProfileIdentity()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SetDisplayNameAsync(sp, "Scott");
        await AddSemanticAsync(sp, "The user's name is Bob.", "name", "Bob");
        var conv = await StartAsync(sp);

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "what is my name?")).Packet.Render();

        Assert.Contains("Name: Scott", rendered);
        Assert.DoesNotContain("Bob", rendered);
        Assert.Contains("withheld from current context", rendered);
    }

    [Fact]
    public async Task Reflection_AppliesOnlyAssistantOwnedCompanionPreferences()
    {
        await using var host = new TestHost(Now, o => o.ReflectionMinNewMessages = 1);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conv = await StartAsync(sp);
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.User, Content = "I love The Godfather.", Timestamp = Now.AddMinutes(-5),
        });
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.Assistant, Content = "I still prefer Alien.", Timestamp = Now.AddMinutes(-4),
        });

        const string reflection =
            """
            {"musing":"The movie exchange stuck with me.",
             "curiosities":[],
             "sharedMoments":[],
             "preferences":[
               {"owner":"Companion","subject":"The Godfather","feeling":"like","strength":"strong","reason":"wrongly copied from the user","evidence":"I love The Godfather."},
               {"owner":"Companion","subject":"Alien","feeling":"like","strength":"moderate","reason":"I said I still preferred it","evidence":"I still prefer Alien."},
               {"owner":"User","subject":"The Godfather","feeling":"like","strength":"strong","reason":"the user said so","evidence":"I love The Godfather."}
             ],
             "settled":[]}
            """;

        var result = await BuildReflector(sp, reflection).ReflectAsync(User);

        var pref = Assert.Single(result!.Preferences);
        Assert.Equal("Alien", pref.Subject);
        Assert.Equal("Alien", Assert.Single(await sp.GetRequiredService<IPreferenceStore>().GetAllAsync(User)).Subject);
    }

    [Fact]
    public async Task PrivateConversation_DoesNotPersistProfileMutations()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);
        await sp.GetRequiredService<IConversationStore>().SetDoNotRememberAsync(conv, User, true);

        var reply = await sp.GetRequiredService<IAgent>().HandleAsync(User, conv, "your name is Nova");
        var profile = await sp.GetRequiredService<IProfileStore>().GetOrCreateAsync(User);

        Assert.Equal(IntentKind.SetIdentity, reply.Intent);
        Assert.Contains("private", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(profile.CompanionName);
    }

    [Fact]
    public async Task IdentityProjection_SurvivesFreshScopeReload()
    {
        await using var host = new TestHost(Now);
        Guid conv;
        using (var setup = host.CreateScope())
        {
            var sp = setup.ServiceProvider;
            await SetDisplayNameAsync(sp, "Scott");
            await sp.GetRequiredService<IProfileStore>().SetIdentityAsync(User, "Ava", "female", "she/her");
            conv = await StartAsync(sp);
            await sp.GetRequiredService<IConversationStore>().AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
                Role = MessageRole.User, Content = "prior reload check", Timestamp = Now.AddMinutes(-2),
            });
        }

        using var verify = host.CreateScope();
        var rendered = (await verify.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "still know who we are?")).Packet.Render();

        Assert.Contains("Name: Scott", rendered);
        Assert.Contains("Name: Ava", rendered);
        Assert.Contains("[USER - Scott]", rendered);
    }
}
