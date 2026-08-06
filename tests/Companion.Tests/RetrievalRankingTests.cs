using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class RetrievalRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<TestHost> SeededHostAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<CompanionSeeder>();
        Assert.True(await seeder.SeedAsync(Now));
        return host;
    }

    [Fact]
    public async Task ObliqueReference_RanksTheOpenLoop_First()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        // Scenario A: no project named, relies on continuity signals.
        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "I finally tested that board at home.");

        Assert.NotEmpty(outcome.Selected);
        var top = outcome.Selected[0].Memory.Content;
        Assert.Contains("Jetson", top, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test", top, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelevantMemory_OutranksUnrelatedNoise()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "I finally tested that board at home.");

        double ScoreContaining(string needle) => outcome.Selected
            .Where(r => r.Memory.Content.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Score)
            .DefaultIfEmpty(0)
            .Max();

        // The Jetson testing open loop must beat the unrelated cooking preference.
        Assert.True(ScoreContaining("Jetson") > ScoreContaining("cooking"));
    }

    [Fact]
    public async Task AmbiguousBoardReference_SurfacesTheLiteralBoardMemory_NotUnrelatedFacts()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "That board finally arrived.");

        var contents = outcome.Selected.Select(r => r.Memory.Content).ToList();

        // The buoy's sensor board is the only memory whose text is actually about a board — it
        // surfaces. The word "board" links to the Jetson project only through a project *alias*,
        // not through any Jetson memory's content, so the retriever (which scores content) must not
        // inject the unrelated Jetson deployment memory here; the buoy-vs-Jetson ambiguity is
        // surfaced by the resolver's clarification question instead (see ScenarioTests.C_… ).
        Assert.Contains(contents, c => c.Contains("buoy", StringComparison.OrdinalIgnoreCase));

        // And an unrelated preference (cooking) must not ride in on recency/importance.
        Assert.DoesNotContain(contents, c => c.Contains("cooking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EverySelectedResult_IsExplained()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "How is the Jetson project going?");

        Assert.All(outcome.Selected, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.NotEmpty(r.Signals);
            Assert.Contains(r.Signals.Values, v => v > 0); // at least one positive signal
        });
    }

    [Fact]
    public async Task DetectedProject_BoostsAssociatedMemories()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        // Same query, with and without the resolved project passed in.
        var withProject = await retriever.RetrieveAsync(
            CompanionSeeder.DemoUserId, "any hardware updates?", CompanionSeeder.JetsonProject);

        double JetsonScore(RetrievalOutcome o) => o.Selected
            .Where(r => string.Equals(r.Memory.RelatedProject, CompanionSeeder.JetsonProject, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Score).DefaultIfEmpty(0).Max();
        var plain = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "any hardware updates?");

        Assert.True(JetsonScore(withProject) > JetsonScore(plain));
    }

    [Fact]
    public async Task RecentImportantButUnrelatedMemory_IsNotInjected()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();

        // A brand-new, maximally important, high-confidence fact with no topical link to the query.
        // Before the relevance floor its recency+importance+confidence alone cleared MinScore and it
        // bled into every turn — the "it keeps adding things it knows about me" bug.
        const string fact = "The user is allergic to peanuts.";
        await store.AddSemanticAsync(new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = CompanionSeeder.DemoUserId,
            Subject = "user",
            Predicate = "allergic_to",
            Value = "peanuts",
            NormalizedFact = fact,
            Confidence = 1.0,
            Importance = 1.0,
            Status = MemoryStatus.Active,
            LastConfirmed = Now,
            CreatedAt = Now,
            Embedding = await embeddings.EmbedAsync(fact),
        });

        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        var outcome = await retriever.RetrieveAsync(
            CompanionSeeder.DemoUserId, "How is the Jetson board coming along?");

        bool Mentions(RetrievalResult r) =>
            r.Memory.Content.Contains("peanut", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(outcome.Selected, Mentions); // not admitted on recency/importance alone
        Assert.Contains(outcome.Excluded, Mentions);        // still scored + explainable, just not injected
    }

    [Fact]
    public async Task RetrievalRespectsTopK()
    {
        var host = new TestHost(Now, o => o.TopK = 2);
        await using var _ = host;
        using (var seedScope = host.CreateScope())
        {
            var seeder = seedScope.ServiceProvider.GetRequiredService<CompanionSeeder>();
            await seeder.SeedAsync(Now);
        }

        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "Jetson board testing at home");

        Assert.True(outcome.Selected.Count <= 2);
    }
}
