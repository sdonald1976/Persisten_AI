using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ConsolidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "u1";

    private static async Task AddPreferenceAsync(IServiceProvider sp, string fact, int monthsAgo)
    {
        var store = sp.GetRequiredService<IMemoryStore>();
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var when = Now.AddMonths(-monthsAgo);
        var m = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = User,
            Subject = "user",
            Predicate = "prefers",
            Value = fact,
            NormalizedFact = fact,
            Confidence = 0.7,
            Importance = 0.5,
            Status = MemoryStatus.Active,
            Origin = MemoryOrigin.Stated,
            FirstObserved = when,
            LastConfirmed = when,
            CreatedAt = when,
            Embedding = await embeddings.EmbedAsync(fact),
        };
        m.Evidence.Add(new MemoryEvidence { MessageId = Guid.NewGuid(), Excerpt = fact, Weight = 1.0 });
        await store.AddSemanticAsync(m);
    }

    private static async Task SeedCookingClusterAsync(IServiceProvider sp)
    {
        await AddPreferenceAsync(sp, "The user prefers cooking instructions with exact temperatures.", 6);
        await AddPreferenceAsync(sp, "The user prefers cooking instructions with precise timing.", 4);
        await AddPreferenceAsync(sp, "The user prefers detailed cooking instructions.", 2);
    }

    [Fact]
    public async Task RepeatedMemories_AreConsolidated_PreservingEvidenceAndOriginals()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IMemoryStore>();
        await SeedCookingClusterAsync(sp);

        var result = await sp.GetRequiredService<IMemoryConsolidator>().ConsolidateAsync(User);

        var group = Assert.Single(result.Created);
        Assert.Equal(3, group.SourceMemoryIds.Count);
        Assert.True(group.EvidenceCount >= 3); // union of supporting evidence preserved
        Assert.Contains("cooking", group.Content, StringComparison.OrdinalIgnoreCase);

        var all = await store.GetRetrievableMemoriesAsync(User);
        var consolidated = all.OfType<SemanticMemory>().Single(m => m.Origin == MemoryOrigin.Consolidated);
        Assert.Equal(MemoryStatus.Active, consolidated.Status);
        Assert.True((await store.GetEvidenceAsync(User, consolidated.Id)).Count >= 3);

        // Originals are retained (not destroyed).
        Assert.Equal(3, all.OfType<SemanticMemory>().Count(m => m.Origin == MemoryOrigin.Stated));
    }

    [Fact]
    public async Task ConsolidatedMemory_IsLabeledInferred_NotADirectStatement()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedCookingClusterAsync(sp);
        await sp.GetRequiredService<IMemoryConsolidator>().ConsolidateAsync(User);

        var consolidated = (await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User))
            .OfType<SemanticMemory>().Single(m => m.Origin == MemoryOrigin.Consolidated);

        var assembler = sp.GetRequiredService<IContextAssembler>();
        var result = new RetrievalResult
        {
            Memory = consolidated,
            Score = 1.0,
            Signals = new Dictionary<string, double> { ["x"] = 1 },
            Reason = "x",
        };
        var packet = assembler.Assemble("hi", Array.Empty<Message>(), new[] { result }, ProjectContext.Empty);

        // A consolidated fact is system-generated, so it must not be presented as a direct quote.
        Assert.Equal(ContextProvenance.Inferred, packet.Memories.Single().Provenance);
    }

    [Fact]
    public async Task Consolidation_IsIdempotent()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await SeedCookingClusterAsync(sp);
        var consolidator = sp.GetRequiredService<IMemoryConsolidator>();

        Assert.Single((await consolidator.ConsolidateAsync(User)).Created);
        Assert.Empty((await consolidator.ConsolidateAsync(User)).Created); // second pass adds nothing
    }

    [Fact]
    public async Task DoesNotOvergeneralize_FromTooFewObservations()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        await AddPreferenceAsync(sp, "The user prefers cooking instructions with exact temperatures.", 6);
        await AddPreferenceAsync(sp, "The user prefers detailed cooking instructions.", 2);

        var result = await sp.GetRequiredService<IMemoryConsolidator>().ConsolidateAsync(User);
        Assert.Empty(result.Created); // only two observations — below the min
    }
}
