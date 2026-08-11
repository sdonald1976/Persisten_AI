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

public class BehaviorExpansionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> StartAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

    private static async Task<SemanticMemory> AddFactAsync(IServiceProvider sp, string content, string value)
    {
        var memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = "topic",
            Value = value,
            NormalizedFact = content,
            Confidence = 0.9,
            Importance = 0.9,
            Status = MemoryStatus.Active,
            Validity = Validity.Current,
            FirstObserved = Now.AddDays(-1),
            LastConfirmed = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1),
            Embedding = await sp.GetRequiredService<IEmbeddingModel>().EmbedAsync(content),
        };
        await sp.GetRequiredService<IMemoryStore>().AddSemanticAsync(memory);
        return memory;
    }

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

    [Fact]
    public async Task Attention_Strengthens_Expires_AndIsCapped()
    {
        await using var host = new TestHost(Now, o => { o.MaxAttentionItems = 3; o.AttentionTtlDays = 1; });
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "I'm testing the identity hardening changes today.");
        var first = Assert.Single(await sp.GetRequiredService<IAttentionStore>().GetActiveAsync(User, 10));
        var strength = first.Strength;

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "More testing on identity hardening is important.");
        var reinforced = Assert.Single(await sp.GetRequiredService<IAttentionStore>().GetActiveAsync(User, 10));
        Assert.True(reinforced.Strength > strength);

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "debug deploy review important");
        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "workflow blocked changed testing");
        Assert.Equal(3, (await sp.GetRequiredService<IAttentionService>().SelectForContextAsync(User, "anything", 3)).Count);

        host.Clock.Advance(TimeSpan.FromDays(2));
        await sp.GetRequiredService<IAttentionStore>().ExpireOldAsync(User, host.Clock.GetUtcNow());
        Assert.Empty(await sp.GetRequiredService<IAttentionStore>().GetActiveAsync(User, 10));
    }

    [Fact]
    public async Task AssociativeRecall_AddsBoundedRelatedMemoryOnly()
    {
        await using var host = new TestHost(Now, o => { o.TopK = 1; o.MaxAssociativeMemories = 1; });
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await AddFactAsync(sp, "The user is testing identity hardening.", "identity hardening");
        var b = await AddFactAsync(sp, "The user previously fixed pronoun reversal bugs.", "pronoun reversal");
        await AddFactAsync(sp, "The user enjoys sourdough bread.", "sourdough");
        await sp.GetRequiredService<IMemoryAssociationStore>().AddValidatedAsync(new MemoryAssociation
        {
            UserId = User,
            SourceMemoryId = a.Id,
            TargetMemoryId = b.Id,
            AssociationType = MemoryAssociationType.Correction,
            Strength = 0.9,
            Evidence = "both are identity consistency corrections",
            CreatedAt = Now,
            LastReinforcedAt = Now,
        });
        var conv = await StartAsync(sp);

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "identity hardening")).Packet.Render();

        Assert.Contains("pronoun reversal", rendered);
        Assert.Contains("Associative", rendered);
        Assert.DoesNotContain("sourdough", rendered);
    }

    [Fact]
    public async Task AssociationWithoutEvidence_IsRejected()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await AddFactAsync(sp, "A", "A");
        var b = await AddFactAsync(sp, "B", "B");

        var added = await sp.GetRequiredService<IMemoryAssociationStore>().AddValidatedAsync(new MemoryAssociation
        {
            UserId = User,
            SourceMemoryId = a.Id,
            TargetMemoryId = b.Id,
            Strength = 0.9,
            Evidence = "",
            CreatedAt = Now,
            LastReinforcedAt = Now,
        });

        Assert.Null(added);
    }

    [Fact]
    public async Task SharedPerspective_PreservesEvent_AndRejectsInventedUserPerspective()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var episode = new EpisodicMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Description = "Scott and Ava debugged identity issues.",
            Owner = MemoryOwner.Shared,
            EventTime = Now.AddDays(-1),
            MentionedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1),
            Status = MemoryStatus.Active,
            Confidence = 0.9,
            Importance = 0.9,
            Embedding = await sp.GetRequiredService<IEmbeddingModel>().EmbedAsync("identity debug"),
        };
        episode.Evidence.Add(new MemoryEvidence { Excerpt = "debugged identity issues together", Weight = 1 });
        await sp.GetRequiredService<IMemoryStore>().AddEpisodicAsync(episode);

        var store = sp.GetRequiredService<ISharedPerspectiveStore>();
        Assert.Null(await store.AddValidatedAsync(new SharedExperiencePerspective
        {
            UserId = User,
            ExperienceId = episode.Id,
            Owner = MemoryOwner.User,
            Summary = "Scott was frustrated.",
            Evidence = "frustrated",
            Confidence = 0.7,
            CreatedAt = Now,
        }));
        Assert.NotNull(await store.AddValidatedAsync(new SharedExperiencePerspective
        {
            UserId = User,
            ExperienceId = episode.Id,
            Owner = MemoryOwner.Companion,
            Summary = "Ava considered identity bugs important for continuity.",
            Evidence = "reflection",
            Confidence = 0.8,
            CreatedAt = Now,
        }));

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, await StartAsync(sp), "identity debug")).Packet.Render();
        Assert.Contains("Scott and Ava debugged identity issues.", rendered);
        Assert.Contains("Ava perspective", rendered);
    }

    [Fact]
    public async Task ExplicitTeaching_CreatesProcedure_CasualExplanationDoesNot_AndRevisionPreservesHistory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv,
            "This is how I make my ribeye: 1. salt it early 2. sear it hard 3. baste with butter.");
        var procedures = await sp.GetRequiredService<IProcedureStore>().SearchAsync(User, "ribeye", 5);
        var procedure = Assert.Single(procedures);
        Assert.Equal(3, procedure.Steps.Count(s => s.IsActive));

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "I explain steak to friends sometimes.");
        Assert.Single(await sp.GetRequiredService<IProcedureStore>().SearchAsync(User, "ribeye", 5));

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "I don't do step 3 anymore for ribeye.");
        procedure = Assert.Single(await sp.GetRequiredService<IProcedureStore>().SearchAsync(User, "ribeye", 5));
        Assert.Equal(2, procedure.Steps.Count(s => s.IsActive));
        Assert.NotEmpty(await sp.GetRequiredService<IProcedureStore>().GetRevisionsAsync(User, procedure.Id));

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "How do I make my ribeye again?")).Packet.Render();
        Assert.Contains("USER-TAUGHT PROCEDURES", rendered);
        Assert.Contains("authoritative user-taught workflow", rendered);
    }

    [Fact]
    public async Task CapabilityRegistry_RendersTruthfully_AndVerificationUpdatesState()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();

        var summary = await registry.RenderSummaryAsync();
        Assert.Contains("converse with the user", summary);
        Assert.Contains("browse arbitrary websites", summary);

        await registry.MarkSuccessAsync("image.describe", Now);
        var image = (await registry.GetAllAsync()).Single(c => c.Id == "image.describe");
        Assert.Equal(CapabilityAvailability.Available, image.Availability);

        await registry.MarkFailureAsync("image.describe", Now);
        await registry.MarkFailureAsync("image.describe", Now);
        await registry.MarkFailureAsync("image.describe", Now);
        image = (await registry.GetAllAsync()).Single(c => c.Id == "image.describe");
        Assert.Equal(CapabilityAvailability.Unavailable, image.Availability);
    }

    [Fact]
    public async Task CapabilityAwareCallback_ContextShowsPastLimitationAndCurrentCapability()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await AddFactAsync(sp, "Ava previously could not identify objects in Scott's image because object detection was unavailable.", "object detection unavailable");
        await sp.GetRequiredService<ICapabilityRegistry>().MarkSuccessAsync("object_detection", Now);

        var rendered = (await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, await StartAsync(sp), "Can you identify objects in images now?")).Packet.Render();

        Assert.Contains("previously could not identify objects", rendered);
        Assert.Contains("identify objects in images", rendered);
    }

    [Fact]
    public async Task PrivateTurns_CreateNoAttentionOrProcedures()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);
        await sp.GetRequiredService<IConversationStore>().SetDoNotRememberAsync(conv, User, true);

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv,
            "This is how I make my ribeye: 1. salt 2. sear. I'm testing important workflow changes.");

        Assert.Empty(await sp.GetRequiredService<IAttentionStore>().GetActiveAsync(User, 10));
        Assert.Empty(await sp.GetRequiredService<IProcedureStore>().SearchAsync(User, "ribeye", 10));
    }

    [Fact]
    public async Task ReflectionCandidates_ArePersistedOnlyAfterValidation()
    {
        await using var host = new TestHost(Now, o => o.ReflectionMinNewMessages = 1);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);
        var conversations = sp.GetRequiredService<IConversationStore>();
        var userMessage = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.User,
            Content = "This is how I summarize logs: first capture the error, second group repeated lines, finally list the next action.",
            Timestamp = Now.AddMinutes(-5),
        };
        await conversations.AddMessageAsync(userMessage);
        await conversations.AddMessageAsync(new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.Assistant,
            Content = "That log workflow seems useful to keep in mind.",
            Timestamp = Now.AddMinutes(-4),
        });
        var a = await AddFactAsync(sp, "The user has a log summarization workflow.", "log workflow");
        var b = await AddFactAsync(sp, "The user groups repeated log lines before deciding next actions.", "repeated log lines");
        _ = a;
        _ = b;

        const string reflection =
            """
            {"musing":"The log workflow is worth holding onto.",
             "curiosities":[],
             "sharedMoments":[],
             "preferences":[],
             "attentionCandidates":[{"subject":"log workflow","summary":"Scott is teaching Ava his log summarization process.","strength":0.7,"evidence":"This is how I summarize logs"}],
             "associationCandidates":[{"sourceMemory":"log summarization workflow","targetMemory":"groups repeated log lines","associationType":"FollowUp","strength":0.8,"evidence":"group repeated lines"}],
             "procedureCandidates":[{"text":"This is how I summarize logs: first capture the error, second group repeated lines, finally list the next action.","evidence":"This is how I summarize logs"}],
             "sharedPerspectiveCandidates":[{"experience":"missing shared event","owner":"User","summary":"invented perspective","confidence":0.9,"evidence":"not real"}],
             "settled":[]}
            """;

        var result = await BuildReflector(sp, reflection).ReflectAsync(User);

        Assert.Single(result!.AttentionItems);
        Assert.Single(result.Associations);
        var procedure = Assert.Single(result.Procedures);
        Assert.Equal(3, procedure.Steps.Count);
        Assert.Empty(result.SharedPerspectives);
    }

    [Fact]
    public async Task ProcedureParsing_HandlesOrdinalAndSemicolonSteps()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await StartAsync(sp);

        await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv,
            "This is how I summarize logs: first capture the error; second group repeated lines; finally list the next action.");

        var procedure = Assert.Single(await sp.GetRequiredService<IProcedureStore>().SearchAsync(User, "summarize logs", 3));
        Assert.Equal("Summarize Logs", procedure.Name);
        Assert.Equal(3, procedure.Steps.Count);
        Assert.Contains(procedure.Steps, s => s.Instruction.Contains("capture the error"));
    }
}
