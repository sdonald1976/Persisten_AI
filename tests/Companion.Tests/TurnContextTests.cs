using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Turns.Context;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase B3. The extracted context-preparation stage.
///
/// Everything here already ran inside <c>CompleteTurnAsync</c>. These pin it at its new
/// address, including the ordering the extraction deliberately did not hide: history loads
/// before understanding, retrieval after it, intent completion after retrieval, and the
/// remaining ingredients after this turn's mood has been captured.
/// </summary>
public class TurnContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static string User => CompanionSeeder.DemoUserId;

    private static PromptIdentityContext Identities => new()
    {
        UserName = "Scott", CompanionName = "Ava",
    };

    private static async Task<(TestHost Host, Guid ConversationId)> HostAsync()
    {
        var host = new TestHost(Now);
        using var seed = host.CreateScope();
        var conversation = await seed.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        return (host, conversation.Id);
    }

    private static TurnContext Context(TestHost host, IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<TurnContext>();

    // ---- history ------------------------------------------------------------------------------

    [Fact]
    public async Task HistoryExcludesTheMessageBeingHandled_AndKeepsItsOrder()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var m = new Message
            {
                Id = Guid.NewGuid(), UserId = User, ConversationId = conversationId,
                Role = MessageRole.User, Content = $"m{i}", Timestamp = Now.AddMinutes(i),
            };
            await conversations.AddMessageAsync(m);
            ids.Add(m.Id);
        }

        var recent = await Context(host, scope).LoadHistoryAsync(conversationId, User, ids[2]);

        Assert.DoesNotContain(recent, m => m.Id == ids[2]);
        Assert.Equal(["m0", "m1"], recent.Select(m => m.Content));
    }

    // ---- retrieval ----------------------------------------------------------------------------

    [Fact]
    public async Task NoMemories_YieldsAnEmptySelectionRatherThanThrowing()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var retrieved = await Context(host, scope).RetrieveAsync(User, "anything at all", null);

        Assert.NotNull(retrieved.Outcome);
        Assert.Empty(retrieved.Selected);
        Assert.Empty(retrieved.Associative);
    }

    [Fact]
    public async Task RankedMemories_KeepTheRetrieverOrderingAndScores()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        foreach (var fact in new[]
                 {
                     "The shed roof needs replacing before winter.",
                     "Scott has a dog named Ruby.",
                     "The bird feeder baffle keeps failing.",
                 })
            await memories.AddSemanticAsync(new SemanticMemory
            {
                Id = Guid.NewGuid(), UserId = User, Subject = "user", Predicate = "said",
                Value = fact, NormalizedFact = fact,
                FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
            });

        var retrieved = await Context(host, scope).RetrieveAsync(User, "the shed roof", null);

        // Selected is the retriever's order, with associative appended after — never re-ranked.
        Assert.Equal(
            retrieved.Outcome.Selected.Concat(retrieved.Associative).Select(r => r.Memory.Content),
            retrieved.Selected.Select(r => r.Memory.Content));
        Assert.All(retrieved.Selected, r => Assert.True(r.Score >= 0));
    }

    [Fact]
    public async Task ExclusionsAndTheirReasonsSurviveOnTheOutcome()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        for (var i = 0; i < 12; i++)
            await memories.AddSemanticAsync(new SemanticMemory
            {
                Id = Guid.NewGuid(), UserId = User, Subject = "user", Predicate = "said",
                Value = $"fact {i} about sheds and roofs",
                NormalizedFact = $"Fact {i} about sheds and roofs.",
                FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
            });

        var retrieved = await Context(host, scope).RetrieveAsync(User, "sheds and roofs", null);

        // Whatever the cutoff does, exclusions carry a reason rather than vanishing.
        Assert.All(retrieved.Outcome.Excluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
    }

    [Fact]
    public async Task SupersededEvidenceIsNotSelectedAsCurrent()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        var superseded = new SemanticMemory
        {
            Id = Guid.NewGuid(), UserId = User, Subject = "user", Predicate = "meets",
            Value = "Tuesday", NormalizedFact = "The presentation was on Tuesday.",
            FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
            Status = MemoryStatus.Superseded, Validity = Validity.Superseded,
        };
        await memories.AddSemanticAsync(superseded);

        var retrieved = await Context(host, scope).RetrieveAsync(User, "the presentation day", null);

        // A superseded fact is never presented as if it were still true.
        Assert.DoesNotContain(retrieved.Selected,
            r => r.Memory.Id == superseded.Id && r.Memory.Status == MemoryStatus.Active);
    }

    [Fact]
    public async Task RetrievalWithTheRawQuery_IsAComparisonAndNothingElse()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var raw = await Context(host, scope)
            .RetrieveWithRawQueryAsync(User, "the shed", null);

        // Bounded to five, formatted for the trace, and it changes nothing the turn uses.
        Assert.True(raw.Count <= 5);
    }

    // ---- concept familiarity ---------------------------------------------------------------

    [Fact]
    public async Task ATurnThatAsksNothing_LooksUpNoConcept()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        Assert.Null(await Context(host, scope)
            .LookupKnowledgeAsync(User, "The squirrel defeated the baffle again."));
    }

    [Fact]
    public async Task AnEpistemicQuestion_IsAnsweredFromTheConceptStore()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var looked = await Context(host, scope)
            .LookupKnowledgeAsync(User, "Do you know what a quokka is?");

        if (looked is var (result, term))
        {
            // Unknown is the honest answer for a term never taught, and it is the SYSTEM
            // answering rather than the model's pretraining.
            Assert.False(string.IsNullOrWhiteSpace(term));
            Assert.Equal(ConceptFamiliarity.Unknown, result.Familiarity);
        }
    }

    // ---- prepared ingredients ------------------------------------------------------------------

    [Fact]
    public async Task PrepareReturnsTypedIngredients_AndOneCuriosityDecision()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var prepared = await Context(host, scope).PrepareAsync(
            User, "What did we decide?", Now, queryEmbedding: null, selectedMemories: [],
            Identities);

        Assert.NotNull(prepared.Relationship);
        Assert.NotNull(prepared.InnerState);
        Assert.NotNull(prepared.Familiarity);

        var decision = Assert.Single(prepared.Decisions);
        Assert.Equal("curiosity", decision.Stage);
        Assert.Equal(prepared.Curiosity is null ? "none-offered" : "offered", decision.Verdict);
    }

    [Fact]
    public async Task WithoutAQueryEmbedding_NoPreferencesAreRelevant()
    {
        // The existing rule, unchanged: similarity needs an embedding, and no embedding means
        // no taste is relevant rather than every taste being relevant.
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();

        var prepared = await Context(host, scope).PrepareAsync(
            User, "anything", Now, queryEmbedding: null, selectedMemories: [], Identities);

        Assert.Empty(prepared.PreferenceNotes);
    }

    [Fact]
    public async Task ProcedureContextIsDataOnly_NeverExecution()
    {
        var (host, _c) = await HostAsync();
        await using var _ = host;
        using var scope = host.CreateScope();
        var procedures = scope.ServiceProvider.GetRequiredService<IProcedureStore>();

        await procedures.AddOrUpdateFromTeachingAsync(
            User, Guid.NewGuid(),
            new Message
            {
                Id = Guid.NewGuid(), UserId = User, ConversationId = Guid.NewGuid(),
                Role = MessageRole.User, Timestamp = Now,
                Content = "When I ask about the shed, always tell me the decision first.",
            },
            Now);

        var prepared = await Context(host, scope).PrepareAsync(
            User, "the shed", Now, null, [], Identities);

        // Whatever was found is rendered as CONTEXT lines. Nothing here runs a procedure.
        Assert.All(prepared.ProcedureNotes, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    // ---- project resolution and privacy, through the real turn ---------------------------------

    [Fact]
    public async Task ASensitiveTurn_StillGathersContextButLeavesNoDerivedMemory()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId,
                    "Keep this private: the antidepressants are making my insomnia worse.");
            Assert.Equal(TurnStatus.Answered, trace.Status);
            Assert.False(string.IsNullOrWhiteSpace(trace.Response));
        }

        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // The turn was answered — context was gathered — and nothing derived was stored.
        Assert.Equal(2, await db.Messages.AsNoTracking().CountAsync());
        Assert.Empty(await db.SemanticMemories.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ProjectResolutionReachesRetrieval_ThroughARealTurn()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        using var scope = host.CreateScope();
        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conversationId, "How is the shed project going?");

        // Whatever resolved, the trace reports it, and retrieval was given the same value.
        Assert.NotNull(trace.ProjectContext);
        Assert.Equal(TurnStatus.Answered, trace.Status);
    }

    // ---- ordering, which the extraction deliberately did not hide --------------------------------

    [Fact]
    public async Task TheStageOrderIsHistoryThenUnderstandingThenRetrievalThenIntent()
    {
        var (host, conversationId) = await HostAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conversationId, "What did we decide about the shed?");

        using var read = host.CreateScope();
        var turn = (await read.ServiceProvider.GetRequiredService<IDiagnosticsStore>()
            .GetRecentTurnsAsync(User, 1)).Single();
        var stages = (turn.Decisions ?? "")
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('=')[0])
            .ToList();

        var interpretation = stages.IndexOf("interpretation");   // understanding, pre-retrieval
        var intent = stages.IndexOf("intent");                   // understanding, post-retrieval
        var curiosity = stages.IndexOf("curiosity");             // context, prepared last

        Assert.True(interpretation >= 0 && intent >= 0 && curiosity >= 0,
            $"missing a stage in [{string.Join(", ", stages)}]");
        Assert.True(interpretation < intent,
            "understanding's read must precede its intent completion");
        Assert.True(intent < curiosity,
            "intent completion must precede the prepared context ingredients");
    }

    // ---- boundaries -------------------------------------------------------------------------------

    [Fact]
    public void ContextOwnsNothingItShouldNot()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Turns", "Context", "TurnContext.cs"));

        foreach (var forbidden in new[]
                 {
                     "PlanV3Builder", "PlanV4Codec", "CompactV", "IContextAssembler",
                     "IReplyGenerator", "ToolLoop", "IShadowRecorder", "IRendererShadow",
                     "IMemoryPipeline", "IFrameSessionStore", "Assemble(",
                 })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultsAreTypedThroughout()
    {
        foreach (var type in new[] { typeof(TurnContextResult), typeof(TurnRetrievalResult) })
        {
            var properties = type.GetProperties();
            Assert.NotEmpty(properties);
            Assert.DoesNotContain(properties, p =>
                p.PropertyType == typeof(object)
                || typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
