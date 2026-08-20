using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Persistence;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The Phase-1 extraction boundary: resolved conversational references flow into durable
/// memory; unresolved ones are refused rather than stored as garbage. The reference failure is
/// real and verbatim — the store once gained "The user is planning a small dinner for someone
/// named her." while working context knew perfectly well who "her" was. Expected durable
/// meaning: dinner is for Beth, with provenance to BOTH the dinner utterance and the message
/// that introduced Beth. Guessed resolutions never reach extraction at all.
/// </summary>
public class ReferenceExtractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "ref-user";

    private static Message Msg(Guid conv, MessageRole role, string content, DateTimeOffset at) =>
        new()
        {
            Id = Guid.NewGuid(), ConversationId = conv, UserId = UserId,
            Role = role, Content = content, Timestamp = at,
        };

    // ---- the guard, on its own ----

    [Theory]
    [InlineData("The user is planning a small dinner for someone named her.")] // live specimen #1
    [InlineData("The user has a friend, someone called him.")]
    [InlineData("The user is knitting a scarf for her.")]                      // live specimen #2 — quieter garbage
    [InlineData("The user went hiking with him.")]
    public void PronounAsPerson_IsRefused(string content)
        => Assert.True(UnresolvedReferentGuard.IsPronounAsPerson(
            new MemoryCandidate { Kind = MemoryKind.Semantic, Content = content }));

    [Theory]
    [InlineData("The user named her dog Precious.")]        // pronoun + real object — legitimate
    [InlineData("The user is making dinner for Beth.")]     // resolved — legitimate
    [InlineData("Someone called Herman lives next door.")]  // 'her' inside a name must not trip
    [InlineData("The user walks her dog every morning.")]   // possessive + noun — legitimate
    [InlineData("The user is knitting a scarf for her sister Beth.")] // pronoun followed by its noun
    public void RealSentences_AreNotRefused(string content)
        => Assert.False(UnresolvedReferentGuard.IsPronounAsPerson(
            new MemoryCandidate { Kind = MemoryKind.Semantic, Content = content }));

    [Fact]
    public void AModelSuppliedName_OnAnAmbiguousTurn_IsIdentified()
    {
        // The live specimen: the chat model's reply guessed "Beth" for an ambiguous "her",
        // and the extractor laundered that guess into a fact cited against the user's own
        // pronoun sentence. The name is traceable to nobody's words but the model's.
        var pie = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Content = "The user is baking a pie for someone named Beth.",
            Value = "baking a pie for Beth",
        };

        Assert.Equal("Beth", UnresolvedReferentGuard.NamesSomeoneTheUserDidNot(
            pie, new[] { "I'm baking a pie for her." }));
        Assert.Null(UnresolvedReferentGuard.NamesSomeoneTheUserDidNot(
            pie, new[] { "I'm baking a pie for Beth." }));
    }

    [Fact]
    public async Task AGuessResolution_VetoesCandidates_NamingSomeoneTheUserDidNot()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await conversations.StartConversationAsync(UserId, "t", "mock", "test");
        var pieMsg = Msg(conv.Id, MessageRole.User, "I'm baking a pie for her.", Now);
        await conversations.AddMessageAsync(pieMsg);

        var laundered = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Content = "The user is baking a pie for someone named Beth.",
            Subject = "user",
            Evidence = new[] { new CandidateEvidence(pieMsg.Id, "I'm baking a pie for her.") },
        };
        var pipeline = ActivatorUtilities.CreateInstance<MemoryPipeline>(
            scope.ServiceProvider, new StubExtractor(laundered));

        // The system's own resolution was a guess (two candidates in the window) — withheld
        // from the extractor, active as a veto.
        var guess = new ReferenceResolution("her", "Elin", "guess", null, null);
        var result = await pipeline.ProcessAsync(UserId, new[] { pieMsg }, guess);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MemoryDecisionKind.Rejected, decision.Outcome);
        Assert.Contains("Beth", decision.Reason);
        Assert.Contains("ambiguous", decision.Reason);
    }

    [Fact]
    public void ABarePronounValue_IsRefused()
        => Assert.True(UnresolvedReferentGuard.IsPronounAsPerson(
            new MemoryCandidate { Kind = MemoryKind.Semantic, Content = "dinner plan", Value = "her" }));

    // ---- working context grades its own resolutions ----

    [Fact]
    public void OnePersonInTheWindow_IsUnambiguous_AndCarriesItsSource()
    {
        var beth = new Message
        {
            Id = Guid.NewGuid(), Role = MessageRole.User,
            Content = "My sister Beth is visiting Saturday.",
        };
        var reply = new Message
        {
            Id = Guid.NewGuid(), Role = MessageRole.Assistant,
            Content = "That will be lovely.",
        };

        var state = WorkingContext.Read(new[] { beth, reply }, "I'm making dinner for her.");

        Assert.Equal("Beth", state.ResolvedReference);
        Assert.Equal("unambiguous", state.ResolutionConfidence);
        Assert.Equal(beth.Id, state.ReferentSourceMessageId);
        Assert.Contains("Beth is visiting", state.ReferentSourceExcerpt);
    }

    [Fact]
    public void TwoPeopleInTheWindow_IsAGuess()
    {
        var recent = new[]
        {
            new Message
            {
                Id = Guid.NewGuid(), Role = MessageRole.User,
                Content = "My sisters Beth and Clara are both visiting this weekend.",
            },
        };

        var state = WorkingContext.Read(recent, "I'm making dinner for her.");

        Assert.NotNull(state.ResolvedReference); // retrieval may still use the newest guess
        Assert.Equal("guess", state.ResolutionConfidence);
    }

    // ---- the pipeline consumes a sound resolution, with dual provenance ----

    [Fact]
    public async Task AConsumedResolution_StoresTheResolvedFact_CitingBothUtterances()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await conversations.StartConversationAsync(UserId, "t", "mock", "test");

        var bethMsg = Msg(conv.Id, MessageRole.User, "My sister Beth is visiting Saturday.", Now.AddMinutes(-2));
        var dinnerMsg = Msg(conv.Id, MessageRole.User, "I'm making dinner for her.", Now);
        await conversations.AddMessageAsync(bethMsg);
        await conversations.AddMessageAsync(dinnerMsg);

        // The candidate a resolution-aware extractor produces: the fact states Beth, the
        // evidence quotes the user's actual words.
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Content = "The user is planning a dinner for Beth.",
            Subject = "user",
            Value = "dinner for Beth",
            Evidence = new[] { new CandidateEvidence(dinnerMsg.Id, "I'm making dinner for her.") },
        };
        var pipeline = ActivatorUtilities.CreateInstance<MemoryPipeline>(
            scope.ServiceProvider, new StubExtractor(candidate));

        var resolution = new ReferenceResolution(
            "her", "Beth", "unambiguous", bethMsg.Id, bethMsg.Content);
        var result = await pipeline.ProcessAsync(UserId, new[] { dinnerMsg }, resolution);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MemoryDecisionKind.Accepted, decision.Outcome);

        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var evidence = db.Evidence.Where(e => e.MemoryId == decision.ResultingMemoryId).ToList();
        Assert.Equal(2, evidence.Count);
        Assert.Contains(evidence, e => e.MessageId == dinnerMsg.Id && e.Excerpt.Contains("dinner for her"));
        Assert.Contains(evidence, e => e.MessageId == bethMsg.Id && e.Excerpt.Contains("Beth is visiting"));
    }

    [Fact]
    public async Task TheLiveGarbageSpecimen_IsRejectedByThePipeline()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = await conversations.StartConversationAsync(UserId, "t", "mock", "test");
        var dinnerMsg = Msg(conv.Id, MessageRole.User, "I'm making dinner for her.", Now);
        await conversations.AddMessageAsync(dinnerMsg);

        var garbage = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Content = "The user is planning a small dinner for someone named her.",
            Subject = "user",
            Evidence = new[] { new CandidateEvidence(dinnerMsg.Id, "I'm making dinner for her.") },
        };
        var pipeline = ActivatorUtilities.CreateInstance<MemoryPipeline>(
            scope.ServiceProvider, new StubExtractor(garbage));

        // No resolution arrived (a guess, or nothing) — the fact is unknowable, not storable.
        var result = await pipeline.ProcessAsync(UserId, new[] { dinnerMsg });

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MemoryDecisionKind.Rejected, decision.Outcome);
        Assert.Contains("pronoun", decision.Reason);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(UserId));
    }

    // ---- the LLM extractor is told the resolution, in words ----

    [Fact]
    public async Task TheLlmExtractor_StatesTheResolutionToTheModel()
    {
        var chat = new QueuedChatModel("[]");
        var extractor = new LlmMemoryExtractor(chat, NullLogger<LlmMemoryExtractor>.Instance);
        var msg = new Message
        {
            Id = Guid.NewGuid(), Role = MessageRole.User, Content = "I'm making dinner for her.",
        };

        await extractor.ExtractAsync(UserId, new[] { msg },
            new ReferenceResolution("her", "Beth", "unambiguous", Guid.NewGuid(), "My sister Beth is visiting."));
        var prompt = Assert.Single(chat.UserMessages);
        Assert.Contains("\"her\" refers to \"Beth\"", prompt);
        Assert.Contains("original words", prompt);

        // And without a resolution, no note — the model is never told about machinery it
        // shouldn't be thinking about.
        chat.Enqueue("[]");
        await extractor.ExtractAsync(UserId, new[] { msg });
        Assert.DoesNotContain("refers to", chat.UserMessages[1]);
    }

    // ---- end to end: the ambiguous case must not pick a person ----

    [Fact]
    public async Task Ambiguous_HerWithTwoSisters_NeverReachesExtraction_AndStoresNoPerson()
    {
        await using var host = new TestHost(Now);
        Guid conv;
        using (var scope = host.CreateScope())
            conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(UserId, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(UserId, conv, "My sisters Beth and Clara are both visiting this weekend.");
        }
        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(UserId, conv, "I'm making dinner for her.");
        }

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("guess", turn.WorkingContext!.ResolutionConfidence);
        Assert.Equal("withheld-guess",
            turn.Decisions.Single(d => d.Stage == "reference.extraction").Verdict);

        // Whatever extraction proposed from the dinner turn, it selected nobody: no stored
        // memory attributes the dinner to either sister, and none embeds the bare pronoun.
        using var verify = host.CreateScope();
        var memories = await verify.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(UserId);
        var dinner = memories.Where(m => m.Content.Contains("dinner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dinner, m =>
            m.Content.Contains("Beth", StringComparison.OrdinalIgnoreCase)
            || m.Content.Contains("Clara", StringComparison.OrdinalIgnoreCase)
            || m.Content.Contains("named her", StringComparison.OrdinalIgnoreCase));
    }
}
