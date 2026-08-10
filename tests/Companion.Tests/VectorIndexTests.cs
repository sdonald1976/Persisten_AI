using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Vector;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The in-memory vector index must return exactly what the tables say — no staleness — while
/// avoiding a full BLOB re-read each turn. These tests drive it through the real DI wiring so the
/// write-through hook (memory store → index) and the per-user cold load are both exercised: a
/// memory added, re-embedded, soft-deleted, or made a candidate is reflected the moment it lands,
/// and one user's embeddings never leak into another's search.
/// </summary>
public class VectorIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private static SemanticMemory Fact(string userId, string text, float[] embedding, MemoryStatus status = MemoryStatus.Active)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = "user",
            Predicate = "note",
            Value = text,
            NormalizedFact = text,
            Confidence = 0.9,
            Importance = 0.5,
            Status = status,
            FirstObserved = Now,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = embedding,
        };

    private static async Task<Guid> AddAsync(IServiceProvider sp, SemanticMemory fact)
    {
        await sp.GetRequiredService<IMemoryStore>().AddSemanticAsync(fact);
        return fact.Id;
    }

    private static async Task<IReadOnlyList<VectorHit>> SearchAsync(
        IServiceProvider sp, string userId, float[] query, int topK = 10)
        => await sp.GetRequiredService<IVectorIndex>().SearchAsync(userId, query, topK);

    [Fact]
    public async Task SearchAndMaintenance_ResolveToTheSameSingleton()
    {
        await using var host = new TestHost(Now);
        var index = host.Services.GetRequiredService<IVectorIndex>();
        var maintenance = host.Services.GetRequiredService<IVectorIndexMaintenance>();

        Assert.IsType<InMemoryVectorIndex>(index);
        Assert.Same(index, maintenance); // writes must reach the very instance that searches
    }

    [Fact]
    public async Task ColdLoad_SurfacesRowsWrittenBeforeAnySearch()
    {
        await using var host = new TestHost(Now);
        // Write happens while the user's set is still unloaded, so the first search must cold-load it.
        var id = await AddAsync(host.Services, Fact(UserA, "likes hiking", new[] { 1f, 0f, 0f }));

        var hits = await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f });

        Assert.Contains(hits, h => h.MemoryId == id);
    }

    [Fact]
    public async Task WriteThrough_SurfacesRowsAddedAfterTheSetIsLoaded()
    {
        await using var host = new TestHost(Now);
        // Force a cold load of the (empty) set first, so the next add exercises the write-through path.
        Assert.Empty(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }));

        var id = await AddAsync(host.Services, Fact(UserA, "likes hiking", new[] { 1f, 0f, 0f }));

        var hits = await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f });
        Assert.Contains(hits, h => h.MemoryId == id);
    }

    [Fact]
    public async Task SoftDelete_RemovesFromTheIndexImmediately()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IMemoryStore>();
        var fact = Fact(UserA, "likes hiking", new[] { 1f, 0f, 0f });
        await store.AddSemanticAsync(fact);
        Assert.Contains(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }), h => h.MemoryId == fact.Id);

        fact.Status = MemoryStatus.Deleted;
        await store.UpdateSemanticAsync(fact);

        Assert.DoesNotContain(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }), h => h.MemoryId == fact.Id);
    }

    [Fact]
    public async Task CandidateMemories_AreNeverReturned()
    {
        await using var host = new TestHost(Now);
        var id = await AddAsync(host.Services, Fact(UserA, "unconfirmed guess", new[] { 1f, 0f, 0f }, MemoryStatus.Candidate));

        var hits = await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f });

        Assert.DoesNotContain(hits, h => h.MemoryId == id);
    }

    [Fact]
    public async Task ReEmbedding_ChangesWhatMatches()
    {
        await using var host = new TestHost(Now);
        var store = host.Services.GetRequiredService<IMemoryStore>();
        var fact = Fact(UserA, "likes hiking", new[] { 1f, 0f, 0f });
        await store.AddSemanticAsync(fact);

        // Originally aligned with the x-axis query.
        Assert.Contains(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }), h => h.MemoryId == fact.Id);

        // Re-embed onto the y-axis: the x-axis query should no longer match, the y-axis one should.
        fact.Embedding = new[] { 0f, 1f, 0f };
        await store.UpdateSemanticAsync(fact);

        Assert.DoesNotContain(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }), h => h.MemoryId == fact.Id);
        Assert.Contains(await SearchAsync(host.Services, UserA, new[] { 0f, 1f, 0f }), h => h.MemoryId == fact.Id);
    }

    [Fact]
    public async Task Search_IsScopedToTheUser()
    {
        await using var host = new TestHost(Now);
        var mine = await AddAsync(host.Services, Fact(UserA, "my secret", new[] { 1f, 0f, 0f }));
        await AddAsync(host.Services, Fact(UserB, "their secret", new[] { 1f, 0f, 0f }));

        var forB = await SearchAsync(host.Services, UserB, new[] { 1f, 0f, 0f });

        Assert.DoesNotContain(forB, h => h.MemoryId == mine);
        Assert.Single(forB); // only user B's own memory
    }

    [Fact]
    public async Task Search_RanksByCosineAndHonorsTopK()
    {
        await using var host = new TestHost(Now);
        var aligned = await AddAsync(host.Services, Fact(UserA, "dead on", new[] { 1f, 0f, 0f }));
        var oblique = await AddAsync(host.Services, Fact(UserA, "off angle", new[] { 1f, 1f, 0f }));
        await AddAsync(host.Services, Fact(UserA, "orthogonal", new[] { 0f, 1f, 0f })); // cosine 0 → dropped

        var top2 = await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }, topK: 2);

        Assert.Equal(2, top2.Count);
        Assert.Equal(aligned, top2[0].MemoryId); // exact match ranks first
        Assert.Equal(oblique, top2[1].MemoryId);
        Assert.True(top2[0].Similarity > top2[1].Similarity);
    }

    [Fact]
    public async Task EmptyOrNonPositiveTopK_ReturnsNothing()
    {
        await using var host = new TestHost(Now);
        await AddAsync(host.Services, Fact(UserA, "anything", new[] { 1f, 0f, 0f }));

        Assert.Empty(await SearchAsync(host.Services, UserA, new[] { 1f, 0f, 0f }, topK: 0));
    }
}
