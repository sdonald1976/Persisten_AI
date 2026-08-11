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
/// The roleplay guard: she can enjoy in-character play without believing it. Turns flagged as
/// in-character (RP markup, or relationship words the persona has claimed) leave no durable
/// derived memory; candidates that reference the persona are rejected at the pipeline; and with
/// no persona claims, ordinary family talk stays ordinary biography.
/// </summary>
public class RoleplayGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;
    private const string SisterPersona = "You are Ava. Ava is the user's sister; the user is her brother.";

    // ---- the lexicon + detector (pure) ----

    [Fact]
    public void TheLexicon_FindsClaimedRelationships_WithDiminutives()
    {
        var lexicon = PersonaLexicon.From("Ava", SisterPersona);
        Assert.True(lexicon.HasRelationshipNouns);
        Assert.True(lexicon.MentionsRelationship("hey sis, missed you"));
        Assert.True(lexicon.MentionsRelationship("my sister is the best"));
        Assert.True(lexicon.MentionsCompanion("Ava promised me"));
        Assert.False(lexicon.MentionsRelationship("I deployed the service today"));
    }

    [Fact]
    public void WithoutPersonaClaims_FamilyTalk_IsJustBiography()
    {
        var lexicon = PersonaLexicon.From("Ava", "You are a warm, genuinely curious companion.");
        Assert.False(InCharacterDetector.IsInCharacter("my sister called me today", lexicon));
    }

    [Fact]
    public void ActionMarkup_IsAlwaysInCharacter()
    {
        var noClaims = PersonaLexicon.From("Ava", null);
        Assert.True(InCharacterDetector.IsInCharacter("*hugs you tight* good morning!", noClaims));
    }

    [Fact]
    public void WithASisterPersona_SisterTalk_IsInCharacter()
    {
        var lexicon = PersonaLexicon.From("Ava", SisterPersona);
        Assert.True(InCharacterDetector.IsInCharacter("I'm so happy to see you sis!", lexicon));
        // Ambiguous by design — when she IS "your sister", integrity wins over a maybe-fact.
        Assert.True(InCharacterDetector.IsInCharacter("my sister always knows what to say", lexicon));
        Assert.False(InCharacterDetector.IsInCharacter("I have an interview tomorrow", lexicon));
    }

    // ---- through the real turn pipeline ----

    [Fact]
    public async Task AnInCharacterTurn_LeavesNoDurableDerivedMemory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IProfileStore>().SetPersonaAsync(User, SisterPersona);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "I'm so happy to see you sis! today was amazing");

        // Full reply, zero derived memory: no extraction, no mood signal, nothing.
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));
        Assert.Empty(trace.Extraction.Decisions);
        Assert.Empty(await sp.GetRequiredService<IEmotionStore>().GetRecentSignalsAsync(User, 10));
    }

    [Fact]
    public async Task ABiographicalTurn_UnderTheSamePersona_IsStillRemembered()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IProfileStore>().SetPersonaAsync(User, SisterPersona);
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        await sp.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "I have an interview tomorrow");

        // No persona words involved → normal life keeps being captured normally.
        Assert.Single(await sp.GetRequiredService<IAnticipationStore>().GetOpenAsync(User));
    }

    // ---- the pipeline-level candidate filter (belt and suspenders) ----

    private sealed class FixedExtractor : IMemoryExtractor
    {
        private readonly IReadOnlyList<MemoryCandidate> _candidates;
        public FixedExtractor(IReadOnlyList<MemoryCandidate> candidates) => _candidates = candidates;
        public Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
            string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default)
            => Task.FromResult(_candidates);
    }

    [Fact]
    public async Task ACandidateReferencingThePersona_IsRejectedByThePipeline()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IProfileStore>().SetPersonaAsync(User, SisterPersona);

        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;
        var message = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = User,
            Role = MessageRole.User, Content = "my sister always knows what to say", Timestamp = Now,
        };
        await sp.GetRequiredService<IConversationStore>().AddMessageAsync(message);

        // Even if an extractor proposes it (this one is forced to), the fact never lands.
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic, Subject = "user", Predicate = "has_sister",
            Value = "a sister", Content = "The user has a sister.",
            ProposedConfidence = 0.9, Importance = 0.7,
            Evidence = new List<CandidateEvidence> { new(message.Id, "my sister always knows what to say") },
        };
        var pipeline = new MemoryPipeline(
            new FixedExtractor(new[] { candidate }),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<IMemoryCurator>(),
            sp.GetRequiredService<IEmbeddingModel>(),
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IPersonalityService>(),
            sp.GetRequiredService<IOptions<CompanionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<MemoryPipeline>.Instance);

        var result = await pipeline.ProcessAsync(User, new[] { message });

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MemoryDecisionKind.Rejected, decision.Outcome);
        Assert.Contains("persona", decision.Reason);
        Assert.Empty(await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User));
    }
}
