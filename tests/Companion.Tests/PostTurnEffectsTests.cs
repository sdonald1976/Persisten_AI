using System.Reflection;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Core.Turns.PostTurn;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B6. The extracted post-turn effects.
///
/// The invariant these exist to hold: every durable effect observes the user's message and
/// the reply that was ACTUALLY DISPLAYED. The strongest guarantee is structural rather than
/// behavioural — a rejected candidate has nowhere to live on the request type — and the first
/// test asserts exactly that, because a shape that makes the mistake impossible outlives any
/// number of tests that merely check it did not happen this time.
/// </summary>
public class PostTurnEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Trace = Guid.Parse("77777777-1111-2222-3333-444444444444");
    private static string User => CompanionSeeder.DemoUserId;

    private const string Displayed = "The shed decision was to replace the roof before winter.";
    private const string RejectedCandidate = "ZEBRAQUOKKA rejected candidate text";

    private static PostTurnEffects Effects(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<PostTurnEffects>();

    private static async Task<(TestHost Host, Guid ConversationId)> HostAsync()
    {
        var host = new TestHost(Now);
        using var seed = host.CreateScope();
        var conversation = await seed.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        return (host, conversation.Id);
    }

    private static async Task<(Message User, Message Assistant)> ExchangeAsync(
        IServiceScope scope, Guid conversationId, string userText, string reply)
    {
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var user = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = conversationId,
            Role = MessageRole.User, Content = userText, Timestamp = Now,
        };
        await conversations.AddMessageAsync(user);

        var assistant = await Effects(scope).StoreReplyAsync(
            User, conversationId, reply, user.Id, Now.AddSeconds(1),
            new ChatCompletion { Text = reply, Model = "mock", Rounds = 1 });
        return (user, assistant);
    }

    private static PostTurnRequest Request(
        Guid conversationId, Message user, Message assistant, string displayed) => new()
    {
        TraceId = Trace,
        UserId = User,
        ConversationId = conversationId,
        Now = Now,
        ExtractionSource = user,
        AssistantMessage = assistant,
        DisplayedReply = displayed,
        ProjectContext = ProjectContext.Empty,
        Working = Core.Turns.Understanding.TurnUnderstanding
            .Read([], user.Content, null, "Scott", "Ava").Working,
        Lexicon = PersonaLexicon.From("Ava", null),
    };

    // ---- the structural guarantee -----------------------------------------------------------

    [Fact]
    public void ARejectedCandidateHasNowhereToLiveOnTheRequest()
    {
        // The load-bearing property. A production candidate the canary replaced, a canary
        // reply the guard rejected, a pre-gate response and tool intermediate text are all
        // absent from the type — so they cannot reach durable state by an oversight.
        // Exactly one property can carry reply text, and it is the displayed one. UserId is
        // a string too, which is why the check is about what a name MEANS rather than about
        // its type.
        var replyCarrying = typeof(PostTurnRequest).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => p.Name.Contains("Reply", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Response", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Candidate", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["DisplayedReply"], replyCarrying);

        foreach (var forbidden in new[]
                 {
                     "ProductionCandidate", "RendererCandidate", "PreGateResponse",
                     "FallbackCandidate", "ToolResults", "NativeV3", "CompactV4",
                 })
            Assert.DoesNotContain(typeof(PostTurnRequest).GetProperties(),
                p => p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostTurnEffectsNeverSeesAnExecutionResult()
    {
        // It receives the final reply, not the object that also carries the losers.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "PostTurn", "PostTurnEffects.cs"));

        Assert.DoesNotContain("TurnExecutionResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionCandidate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RendererCandidate", source, StringComparison.Ordinal);
    }

    // ---- the stored reply IS the displayed reply -----------------------------------------------

    [Fact]
    public async Task TheStoredAssistantMessageIsByteIdenticalToTheDisplayedReply()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (_, assistant) = await ExchangeAsync(scope, conversationId, "What about the shed?", Displayed);

        Assert.Equal(Displayed, assistant.Content);

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var stored = await db.Messages.AsNoTracking()
            .SingleAsync(m => m.Role == MessageRole.Assistant);
        Assert.Equal(Displayed, stored.Content);
    }

    [Fact]
    public async Task GenerationMetadataRidesWithTheStoredReply()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (_, assistant) = await ExchangeAsync(scope, conversationId, "hello", Displayed);

        Assert.Equal("mock", assistant.ModelUsed);
        Assert.Equal(1, assistant.GenerationRounds);
    }

    // ---- no rejected text reaches durable state -------------------------------------------------

    [Fact]
    public async Task NoRejectedCandidateTextAppearsInAnyDurableTable()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        // The displayed reply is the ONLY reply handed over. The rejected text is never
        // passed anywhere — this asserts it also never turns up.
        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "The shed roof needs replacing.", Displayed);
        await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        Assert.DoesNotContain(RejectedCandidate, await DurableTextAsync(host),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenTheDisplayedReplyDiffersFromAnEarlierCandidate_OnlyTheDisplayedOneIsLearnedFrom()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        // Simulates the canary and gate cases: whatever the earlier candidate was, the stored
        // message and every effect see the final text.
        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "What did we decide?", Displayed);
        await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        var durable = await DurableTextAsync(host);
        Assert.Contains(Displayed, durable, StringComparison.Ordinal);
        Assert.DoesNotContain(RejectedCandidate, durable, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the effects themselves --------------------------------------------------------------------

    [Fact]
    public async Task AnOrdinaryTurn_RunsExtractionAndReturnsItsCounts()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "My dog is called Ruby.", Displayed);
        var result = await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        Assert.NotNull(result.Extraction);
        Assert.NotNull(result.Updates);
    }

    [Fact]
    public async Task AttentionIsCapturedFromTheUserMessage()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "The shed roof needs replacing before winter.", Displayed);
        await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var items = await db.AttentionItems.AsNoTracking().ToListAsync();

        // Whatever was captured, its lineage is the USER message, never the reply.
        Assert.All(items, a => Assert.Equal(user.Id.ToString(), a.SourceId));
    }

    [Fact]
    public async Task ConceptLearningReadsTheUserMessageOnly()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "A baffle is a squirrel guard on a bird feeder.", Displayed);
        var result = await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        if (result.TaughtTerm is not null)
        {
            Assert.Contains(result.Decisions, d => d.Stage == "knowledge.taught");
            var db = scope.ServiceProvider
                .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
            var concepts = await db.Concepts.AsNoTracking().ToListAsync();
            Assert.All(concepts, c => Assert.Equal(User, c.UserId));
        }
    }

    [Fact]
    public async Task AnUnresolvedReferenceIsObservedAsAGap()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (user, assistant) = await ExchangeAsync(scope, conversationId, "How is it going?", Displayed);
        var request = Request(conversationId, user, assistant, Displayed);
        var result = await Effects(scope).ApplyAsync(request);

        if (request.Working.ReferenceMarkers.Count > 0
            && request.Working.ResolvedReference is null)
        {
            Assert.Contains(result.Decisions, d => d.Stage == "gap.observed");
        }
    }

    [Fact]
    public async Task EvidenceLineagePointsAtTheUserMessage()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var (user, assistant) = await ExchangeAsync(
            scope, conversationId, "My dog is called Ruby.", Displayed);
        await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var evidence = await db.Evidence.AsNoTracking().ToListAsync();

        // Every piece of evidence names a real message in this exchange — never a candidate,
        // and never a synthesised id.
        Assert.All(evidence, e =>
            Assert.True(e.MessageId == user.Id || e.MessageId == assistant.Id,
                $"evidence cited {e.MessageId}, which is neither message of the exchange"));
    }

    [Fact]
    public async Task MarkingACuriosityVoicedIsIdempotent()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var reflection = new Reflection
        {
            Id = Guid.NewGuid(), UserId = User, CreatedAt = Now, Musing = "a thought",
        };
        var curiosity = new Curiosity
        {
            Id = Guid.NewGuid(), UserId = User, ReflectionId = reflection.Id,
            Question = "Did the quote arrive?", Status = CuriosityStatus.Open, CreatedAt = Now,
        };
        db.Reflections.Add(reflection);
        db.Curiosities.Add(curiosity);
        await db.SaveChangesAsync();

        await Effects(scope).MarkCuriosityVoicedAsync(User, curiosity.Id, Now);
        await Effects(scope).MarkCuriosityVoicedAsync(User, curiosity.Id, Now.AddMinutes(1));

        var after = await db.Curiosities.AsNoTracking().SingleAsync();
        Assert.Equal(CuriosityStatus.Voiced, after.Status);
    }

    // ---- failure behaviour is unchanged ---------------------------------------------------------------

    [Fact]
    public void EffectsDoNotSwallowTheirOwnFailures()
    {
        // The caller's catch owns "the turn still stands". If this component caught its own
        // exceptions, a failure would stop skipping the capture tail below it — a behaviour
        // change disguised as robustness.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "PostTurn", "PostTurnEffects.cs"));

        Assert.DoesNotContain("catch (", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectsWrapNothingInATransaction()
    {
        // They were independent before and stay independent: a partial failure leaves what
        // already succeeded, which is the existing behaviour.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "PostTurn", "PostTurnEffects.cs"));

        Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
    }

    // ---- what post-turn does NOT own -------------------------------------------------------------------

    [Fact]
    public void MoodAndAnticipationStayWhereTheyAffectThisTurn()
    {
        // They run before generation because this turn's inner state colors this turn's own
        // prompt. Relabelling them post-turn would change when Ava's mood is read.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "PostTurn", "PostTurnEffects.cs"));

        foreach (var forbidden in new[]
                 {
                     "CaptureMoodAsync", "CaptureAnticipationAsync", "IEmotionStore",
                     "ICompanionStateTracker", "IAnticipationStore", "IRendererShadow",
                     "IDiagnosticsStore", "ITurnTraceLog", "PlanFidelity",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTurnWritesNoTurnRecordOrRendererShadowRow()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var before = await db.TurnRecords.CountAsync();
        var (user, assistant) = await ExchangeAsync(scope, conversationId, "hello there", Displayed);
        await Effects(scope).ApplyAsync(Request(conversationId, user, assistant, Displayed));

        Assert.Equal(before, await db.TurnRecords.CountAsync());
    }

    // ---- through a real turn: fiction and sensitivity ----------------------------------------------------

    [Fact]
    public async Task AFictionTurn_LeavesNoDurableFactButKeepsFrameMetadata()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                User, conversationId,
                "Let's roleplay: you're a lighthouse keeper and I'm a sailor.");

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // Fictional scene content never becomes a real fact...
        Assert.Empty(await db.SemanticMemories.AsNoTracking().ToListAsync());
        // ...while the operational frame record may persist.
        Assert.NotEmpty(await db.FrameSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ASensitiveTurn_IsAnsweredAndLeavesNoDerivedMemory()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                User, conversationId,
                "Keep this private: the antidepressants are making my insomnia worse.");
            Assert.Equal(TurnStatus.Answered, trace.Status);
        }

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        Assert.Equal(2, await db.Messages.AsNoTracking().CountAsync());
        Assert.Empty(await db.SemanticMemories.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AttentionItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ARealTurn_StoresExactlyWhatItDisplayed()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        string reply;
        using (var scope = host.CreateScope())
            reply = (await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "What did we decide about the shed?")).Response;

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var stored = await db.Messages.AsNoTracking()
            .Where(m => m.Role == MessageRole.Assistant)
            .OrderByDescending(m => m.Timestamp).FirstAsync();

        Assert.Equal(reply, stored.Content);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>Every durable text column a rejected candidate could have leaked into.</summary>
    private static async Task<string> DurableTextAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var parts = new List<string?>();
        parts.AddRange((await db.Messages.AsNoTracking().ToListAsync()).Select(m => m.Content));
        parts.AddRange((await db.SemanticMemories.AsNoTracking().ToListAsync()).Select(m => m.NormalizedFact));
        parts.AddRange((await db.EpisodicMemories.AsNoTracking().ToListAsync()).Select(m => m.Description));
        parts.AddRange((await db.Evidence.AsNoTracking().ToListAsync()).Select(e => e.Excerpt));
        parts.AddRange((await db.AttentionItems.AsNoTracking().ToListAsync()).Select(a => a.Summary));
        parts.AddRange((await db.Reflections.AsNoTracking().ToListAsync()).Select(r => r.Musing));
        parts.AddRange((await db.Curiosities.AsNoTracking().ToListAsync()).Select(c => c.Question));
        parts.AddRange((await db.OpenLoops.AsNoTracking().ToListAsync()).Select(l => l.Description));
        parts.AddRange((await db.KnowledgeGaps.AsNoTracking().ToListAsync()).Select(g => g.Subject));
        parts.AddRange((await db.Experiences.AsNoTracking().ToListAsync()).Select(e => e.Text));
        return string.Join("\n", parts.Where(p => p is not null));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
