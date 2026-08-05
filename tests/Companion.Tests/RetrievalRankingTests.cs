using Companion.Core.Abstractions;
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
    public async Task AmbiguousBoardReference_SurfacesBothHardwareProjects()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var outcome = await retriever.RetrieveAsync(CompanionSeeder.DemoUserId, "That board finally arrived.");

        var contents = outcome.Selected.Select(r => r.Memory.Content).ToList();
        Assert.Contains(contents, c => c.Contains("Jetson", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(contents, c => c.Contains("buoy", StringComparison.OrdinalIgnoreCase));
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
    public async Task NamedProject_IsDetected()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();

        var outcome = await retriever.RetrieveAsync(
            CompanionSeeder.DemoUserId, "Any update on the buoy sensor platform?");

        Assert.Equal(CompanionSeeder.BuoyProject, outcome.DetectedProject);
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
