using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class CorrectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "u1";

    private static async Task<SemanticMemory> AddFactAsync(
        IServiceProvider sp, string value, string fact, string? project = null,
        MemoryStatus status = MemoryStatus.Active, string predicate = "prefers")
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var m = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = predicate,
            Value = value,
            NormalizedFact = fact,
            Confidence = 0.8,
            Status = status,
            RelatedProject = project,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = await embeddings.EmbedAsync(fact),
        };
        await store.AddSemanticAsync(m);
        return m;
    }

    [Fact]
    public async Task Forget_SoftDeletes_PurgesEmbedding_AndRemovesFromIndex()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();
        var index = sp.GetRequiredService<IVectorIndex>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();

        var m = await AddFactAsync(sp, "windsurfing", "The user enjoys windsurfing.");
        var query = await embeddings.EmbedAsync("windsurfing");
        Assert.Contains(await index.SearchAsync(User, query, 50), h => h.MemoryId == m.Id);

        var ok = await sp.GetRequiredService<IMemoryCurator>().ForgetAsync(User, m.Id, "user asked");
        Assert.True(ok);

        // Gone from retrieval and from the similarity index, and its embedding is purged.
        Assert.DoesNotContain(await store.GetRetrievableMemoriesAsync(User), x => x.Id == m.Id);
        Assert.DoesNotContain(await index.SearchAsync(User, query, 50), h => h.MemoryId == m.Id);

        var reloaded = await store.GetSemanticAsync(m.Id, User);
        Assert.Equal(MemoryStatus.Deleted, reloaded!.Status);
        Assert.Null(reloaded.Embedding);
        Assert.Contains(await store.GetRevisionsAsync(m.Id), r => r.Kind == RevisionKind.Deleted);
    }

    [Fact]
    public async Task Dispute_DemotesMemory_AndRecordsRevision()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        var m = await AddFactAsync(sp, "jazz", "The user likes jazz.");
        await sp.GetRequiredService<IMemoryCurator>().DisputeAsync(User, m.Id, "that's wrong");

        Assert.Equal(MemoryStatus.Disputed, (await store.GetSemanticAsync(m.Id, User))!.Status);
        Assert.Contains(await store.GetRevisionsAsync(m.Id), r => r.Kind == RevisionKind.Disputed);
    }

    [Fact]
    public async Task Correct_UpdatesValue_ReEmbeds_AndAudits()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        var m = await AddFactAsync(sp, "tea", "The user drinks tea.");
        await sp.GetRequiredService<IMemoryCurator>()
            .CorrectSemanticAsync(User, m.Id, "coffee", "The user drinks coffee.");

        var reloaded = await store.GetSemanticAsync(m.Id, User);
        Assert.Equal("coffee", reloaded!.Value);
        Assert.Equal("The user drinks coffee.", reloaded.NormalizedFact);
        Assert.Contains(await store.GetRevisionsAsync(m.Id), r => r.Kind == RevisionKind.Updated);
    }

    [Fact]
    public async Task ReassignProject_MovesMemory_AndAudits()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        // Scenario D: "that was the buoy project, not the AI project."
        var m = await AddFactAsync(sp, "waterproofing", "The user is solving waterproofing.", project: "AI project");
        await sp.GetRequiredService<IMemoryCurator>().ReassignMemoryProjectAsync(User, m.Id, "buoy project");

        var reloaded = await store.GetSemanticAsync(m.Id, User);
        Assert.Equal("buoy project", reloaded!.RelatedProject);
        Assert.Contains(await store.GetRevisionsAsync(m.Id), r => r.Kind == RevisionKind.Updated);
    }

    [Fact]
    public async Task Merge_MovesEvidenceToTarget_AndDeletesSource()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        var target = await AddFactAsync(sp, "cycling", "The user enjoys cycling.");
        var source = await AddFactAsync(sp, "cycling", "The user likes to cycle.");
        await store.AddEvidenceAsync(new[]
        {
            new MemoryEvidence { Id = Guid.NewGuid(), MemoryId = source.Id, MemoryKind = MemoryKind.Semantic,
                MessageId = Guid.NewGuid(), Excerpt = "I like to cycle", Weight = 1.0 },
        });

        await sp.GetRequiredService<IMemoryCurator>().MergeAsync(User, source.Id, target.Id);

        Assert.Equal(MemoryStatus.Deleted, (await store.GetSemanticAsync(source.Id, User))!.Status);
        Assert.NotEmpty(await store.GetEvidenceAsync(target.Id));
        Assert.Contains(await store.GetRevisionsAsync(target.Id), r => r.Kind == RevisionKind.Merged);
    }

    [Fact]
    public async Task ResolveReview_Accept_PromotesCandidate_AndSupersedesConflict()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        var active = await AddFactAsync(sp, "low carb", "The user follows a low-carb diet.", predicate: "diet");
        var candidate = await AddFactAsync(sp, "keto", "The user follows a keto diet.",
            status: MemoryStatus.Candidate, predicate: "diet");

        var ok = await sp.GetRequiredService<IMemoryCurator>().ResolveReviewAsync(User, candidate.Id, accept: true);
        Assert.True(ok);

        Assert.Equal(MemoryStatus.Active, (await store.GetSemanticAsync(candidate.Id, User))!.Status);
        Assert.Equal(MemoryStatus.Superseded, (await store.GetSemanticAsync(active.Id, User))!.Status);
    }

    [Fact]
    public async Task ResolveReview_Reject_DeletesCandidate()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();

        var candidate = await AddFactAsync(sp, "keto", "The user follows a keto diet.",
            status: MemoryStatus.Candidate, predicate: "diet");

        await sp.GetRequiredService<IMemoryCurator>().ResolveReviewAsync(User, candidate.Id, accept: false);

        Assert.Equal(MemoryStatus.Deleted, (await store.GetSemanticAsync(candidate.Id, User))!.Status);
    }

    [Fact]
    public async Task Corrections_AreScopedByUser()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var mine = await AddFactAsync(sp, "sailing", "The user enjoys sailing.");
        // A different user cannot forget my memory.
        var ok = await sp.GetRequiredService<IMemoryCurator>().ForgetAsync("someone-else", mine.Id, "nope");
        Assert.False(ok);
        Assert.Equal(MemoryStatus.Active, (await sp.GetRequiredService<IMemoryStore>().GetSemanticAsync(mine.Id, User))!.Status);
    }
}
