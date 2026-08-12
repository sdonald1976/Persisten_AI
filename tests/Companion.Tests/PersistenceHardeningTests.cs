using System.Reflection;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Backlog items #1/#2/#4: the lightweight retrieval projection stays faithful as the entities
/// grow, a restart rebuilds retrieval from disk, and a turn running alongside the background
/// sleep cycle doesn't corrupt state or deadlock the database.
/// </summary>
public class PersistenceHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The projection lists fields by hand, so a new entity property could silently arrive as
    /// default on every retrieval path. This asserts the projection carries EVERY scalar except
    /// the deliberately-omitted embedding — it fails the moment someone adds a field and forgets.
    /// </summary>
    [Theory]
    [InlineData(typeof(SemanticMemory))]
    [InlineData(typeof(EpisodicMemory))]
    public async Task RetrievalProjection_CarriesEveryScalarExceptTheEmbedding(Type entityType)
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);

        var full = await store.GetRetrievableMemoriesAsync(CompanionSeeder.DemoUserId);
        var light = await store.GetRetrievalCandidatesAsync(CompanionSeeder.DemoUserId);

        Assert.Equal(full.Count, light.Count);
        Assert.NotEmpty(full);

        var scalars = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite
                && p.Name != nameof(IMemory.Embedding)
                && (p.PropertyType.IsValueType || p.PropertyType == typeof(string)))
            .ToList();
        Assert.NotEmpty(scalars);

        foreach (var original in full.Where(m => m.GetType() == entityType))
        {
            var projected = light.Single(m => m.Id == original.Id);
            foreach (var property in scalars)
            {
                Assert.Equal(
                    property.GetValue(original),
                    property.GetValue(projected));
            }
            // …and the blob really is omitted (that is the entire point).
            Assert.Null(projected.Embedding);
            Assert.NotNull(original.Embedding);
        }
    }

    [Fact]
    public async Task RetrievalCandidates_AreUntracked_SoTheyCannotSaveANullEmbedding()
    {
        await using var host = new TestHost(Now);
        using (var seed = host.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        }

        // A fresh scope, so the only thing that could be tracked is what this query loads.
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievalCandidatesAsync(CompanionSeeder.DemoUserId);

        // Nothing from the projection is in the change tracker, so a later SaveChanges on this
        // context cannot write those null embeddings back over the stored vectors.
        Assert.DoesNotContain(db.ChangeTracker.Entries(),
            e => e.Entity is SemanticMemory or EpisodicMemory);
    }

    [Fact]
    public async Task Restart_RebuildsRetrievalFromDisk()
    {
        // A real restart: same database file, brand-new host (so the in-memory vector index is
        // cold and must reload from the tables) — retrieval must still find the same memory.
        var dbPath = Path.Combine(Path.GetTempPath(), $"restart-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            Guid conversationId;
            await using (var first = new TestHost(Now, connectionString: connectionString))
            {
                using var scope = first.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
                conversationId = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                    .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;

                var before = await scope.ServiceProvider.GetRequiredService<IRetriever>()
                    .RetrieveAsync(CompanionSeeder.DemoUserId, "Jetson object detection");
                Assert.NotEmpty(before.Selected);
            }

            await using var second = new TestHost(Now, connectionString: connectionString);
            using var scope2 = second.CreateScope();
            var after = await scope2.ServiceProvider.GetRequiredService<IRetriever>()
                .RetrieveAsync(CompanionSeeder.DemoUserId, "Jetson object detection");

            Assert.NotEmpty(after.Selected);
            Assert.Contains(after.Selected, r => r.Memory.Content.Contains("Jetson", StringComparison.OrdinalIgnoreCase));
            // And the conversation survived the restart too.
            var messages = await scope2.ServiceProvider.GetRequiredService<IConversationStore>()
                .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 10);
            Assert.NotNull(messages);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + "*"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task ATurn_AndTheSleepCycle_CanRunAtTheSameTime()
    {
        // The classic SQLite contention shape: the background sleep cycle writing while the user
        // is mid-turn. Both must complete — no lock exception, no lost reply.
        var dbPath = Path.Combine(Path.GetTempPath(), $"concurrency-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var host = new TestHost(Now, connectionString: $"Data Source={dbPath}");
            Guid conversationId;
            using (var seed = host.CreateScope())
            {
                await seed.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
                conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                    .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
            }

            async Task<TurnTrace> TurnAsync()
            {
                using var scope = host.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                    CompanionSeeder.DemoUserId, conversationId, "I finally tested the Jetson at home.");
            }

            async Task SleepAsync()
            {
                using var scope = host.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISleepCycle>()
                    .RunAsync(CompanionSeeder.DemoUserId);
            }

            var turn = TurnAsync();
            var sleep = SleepAsync();
            await Task.WhenAll(turn, sleep);

            Assert.Equal(TurnStatus.Answered, turn.Result.Status);
            Assert.False(string.IsNullOrWhiteSpace(turn.Result.Response));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + "*"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }
}
