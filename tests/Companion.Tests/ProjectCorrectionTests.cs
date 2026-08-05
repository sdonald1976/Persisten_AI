using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ProjectCorrectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<TestHost> SeededHostAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        return host;
    }

    [Fact]
    public async Task MergeProjects_ReassignsChildrenAndMemories_ThenRemovesSource()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var projects = sp.GetRequiredService<IProjectStore>();
        var memories = sp.GetRequiredService<IMemoryStore>();

        var all = await projects.GetProjectsAsync(User);
        var buoy = all.Single(p => p.Name == CompanionSeeder.BuoyProject);
        var jetson = all.Single(p => p.Name == CompanionSeeder.JetsonProject);

        var ok = await sp.GetRequiredService<IProjectCurator>().MergeProjectsAsync(User, buoy.Id, jetson.Id);
        Assert.True(ok);

        // Source project gone; its open loop and alias now belong to the target.
        Assert.Null(await projects.GetProjectAsync(buoy.Id, User));
        var jetsonLoops = await projects.GetOpenLoopsByProjectAsync(jetson.Id, onlyOpen: false);
        Assert.Contains(jetsonLoops, l => l.Description.Contains("buoy", StringComparison.OrdinalIgnoreCase));
        var jetsonAliases = await projects.GetAliasesByProjectAsync(jetson.Id);
        Assert.Contains(jetsonAliases, a => a.Alias == CompanionSeeder.BuoyProject); // old name kept as alias

        // Memories that named the buoy project are re-pointed to the survivor.
        var retrievable = await memories.GetRetrievableMemoriesAsync(User);
        Assert.DoesNotContain(retrievable, m => m.RelatedProject == CompanionSeeder.BuoyProject);
    }

    [Fact]
    public async Task MergedReference_StillResolves_ToTheSurvivor()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var projects = sp.GetRequiredService<IProjectStore>();
        var all = await projects.GetProjectsAsync(User);
        var buoy = all.Single(p => p.Name == CompanionSeeder.BuoyProject);
        var jetson = all.Single(p => p.Name == CompanionSeeder.JetsonProject);

        await sp.GetRequiredService<IProjectCurator>().MergeProjectsAsync(User, buoy.Id, jetson.Id);

        // "buoy" now resolves to the surviving Jetson project (via the kept alias).
        var resolution = await sp.GetRequiredService<IEntityResolver>().ResolveProjectAsync(User, "the buoy");
        Assert.Equal(jetson.Id, resolution.Best?.Project.Id);
    }

    [Fact]
    public async Task SplitProject_MovesSpecifiedLoopsAndAliases_ToANewProject()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var projects = sp.GetRequiredService<IProjectStore>();

        var jetson = (await projects.GetProjectsAsync(User)).Single(p => p.Name == CompanionSeeder.JetsonProject);
        var loop = (await projects.GetOpenLoopsByProjectAsync(jetson.Id, onlyOpen: true)).Single();

        var created = await sp.GetRequiredService<IProjectCurator>()
            .SplitProjectAsync(User, jetson.Id, "Jetson home testing", new[] { loop.Id }, new[] { "object detection" });

        Assert.Equal("Jetson home testing", created.Name);

        // The moved loop and alias now belong to the new project; the source no longer has them.
        Assert.Contains(await projects.GetOpenLoopsByProjectAsync(created.Id, onlyOpen: false), l => l.Id == loop.Id);
        Assert.Empty(await projects.GetOpenLoopsByProjectAsync(jetson.Id, onlyOpen: true));
        Assert.Contains(await projects.GetAliasesByProjectAsync(created.Id), a => a.Alias == "object detection");
    }
}
