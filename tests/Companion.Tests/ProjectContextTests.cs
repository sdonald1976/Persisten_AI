using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ProjectContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<TestHost> SeededHostAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        return host;
    }

    [Fact]
    public async Task Summary_ReconstructsProjectState()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var buoy = (await sp.GetRequiredService<IProjectStore>().GetProjectsAsync(CompanionSeeder.DemoUserId))
            .Single(p => p.Name == CompanionSeeder.BuoyProject);

        var summary = await sp.GetRequiredService<IProjectContextService>()
            .GetSummaryAsync(CompanionSeeder.DemoUserId, buoy.Id);

        Assert.NotNull(summary);
        Assert.Equal(CompanionSeeder.BuoyProject, summary!.Project.Name);
        Assert.Contains(summary.OpenLoops, l => l.Description.Contains("board", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(summary.RecentEvents);
    }

    [Fact]
    public async Task BuildAsync_ConfidentReference_ReconstructsSummary_AndSurfacesProjectOpenLoops()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var context = await scope.ServiceProvider.GetRequiredService<IProjectContextService>()
            .BuildAsync(CompanionSeeder.DemoUserId, "how's the Jetson project coming along?");

        Assert.Equal(CompanionSeeder.JetsonProject, context.ResolvedProjectName);
        Assert.NotNull(context.Summary);
        Assert.Contains(context.OpenLoops, l =>
            l.OpenLoop.Description.Contains("test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_ObliqueReference_SurfacesRelevantOpenLoopByContent()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();

        // No project named; the Jetson "test at home" loop should still surface on content.
        var context = await scope.ServiceProvider.GetRequiredService<IProjectContextService>()
            .BuildAsync(CompanionSeeder.DemoUserId, "I finally tested it at home");

        Assert.Contains(context.OpenLoops, l =>
            l.OpenLoop.Description.Contains("Jetson", StringComparison.OrdinalIgnoreCase));
    }
}
