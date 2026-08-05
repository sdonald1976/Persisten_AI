using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class UserIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string OtherUser = "someone-else";

    private static async Task SeedBothAsync(IServiceProvider sp)
    {
        var seeder = sp.GetRequiredService<CompanionSeeder>();
        await seeder.SeedAsync(Now);

        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var memories = sp.GetRequiredService<IMemoryStore>();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = OtherUser,
            Subject = "user",
            Predicate = "likes",
            Value = "scuba diving in Australia",
            NormalizedFact = "The other user loves scuba diving in Australia.",
            Confidence = 0.9,
            Importance = 0.5,
            Status = MemoryStatus.Active,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = await embeddings.EmbedAsync("The other user loves scuba diving in Australia."),
        });
    }

    [Fact]
    public async Task Store_DoesNotLeakMemoriesAcrossUsers()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await SeedBothAsync(scope.ServiceProvider);
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

        var demo = await memories.GetRetrievableMemoriesAsync(CompanionSeeder.DemoUserId);
        var other = await memories.GetRetrievableMemoriesAsync(OtherUser);

        Assert.All(demo, m => Assert.Equal(CompanionSeeder.DemoUserId, m.UserId));
        Assert.All(other, m => Assert.Equal(OtherUser, m.UserId));
        Assert.Single(other);
        Assert.DoesNotContain(demo, m => m.Content.Contains("scuba", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VectorIndex_IsScopedByUser()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await SeedBothAsync(scope.ServiceProvider);

        var index = scope.ServiceProvider.GetRequiredService<IVectorIndex>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
        var query = await embeddings.EmbedAsync("scuba diving");

        var otherIds = (await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(OtherUser)).Select(m => m.Id).ToHashSet();

        var hits = await index.SearchAsync(OtherUser, query, topK: 50);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains(h.MemoryId, otherIds));
    }

    [Fact]
    public async Task Retriever_OnlyReturnsRequestingUsersMemories()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await SeedBothAsync(scope.ServiceProvider);

        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        var outcome = await retriever.RetrieveAsync(OtherUser, "Tell me about my hobbies");

        Assert.All(outcome.Selected, r => Assert.Equal(OtherUser, r.Memory.UserId));
        Assert.All(outcome.Excluded, r => Assert.Equal(OtherUser, r.Memory.UserId));
    }
}
