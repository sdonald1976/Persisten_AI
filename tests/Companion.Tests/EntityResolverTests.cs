using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class EntityResolverTests
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
    public async Task ClearAliasReference_ResolvesConfidently()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();

        var resolution = await resolver.ResolveProjectAsync(CompanionSeeder.DemoUserId, "how is the buoy going?");

        Assert.False(resolution.RequiresClarification);
        Assert.NotNull(resolution.Best);
        Assert.Equal(CompanionSeeder.BuoyProject, resolution.Best!.Project.Name);
    }

    [Fact]
    public async Task AliasPhrase_ResolvesToTheRightProject()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();

        var resolution = await resolver.ResolveProjectAsync(CompanionSeeder.DemoUserId, "the Jetson project");

        Assert.Equal(CompanionSeeder.JetsonProject, resolution.Best?.Project.Name);
    }

    [Fact]
    public async Task AmbiguousReference_RequiresClarification_AndRanksBothCandidates()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();

        // Both hardware projects share the "board" alias.
        var resolution = await resolver.ResolveProjectAsync(CompanionSeeder.DemoUserId, "that board finally arrived");

        Assert.True(resolution.RequiresClarification);
        Assert.Null(resolution.Best);
        Assert.True(resolution.Candidates.Count >= 2);
        Assert.NotNull(resolution.ClarificationQuestion);
        Assert.Contains("buoy", resolution.ClarificationQuestion!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jetson", resolution.ClarificationQuestion!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownReference_ResolvesToNothing_WithoutGuessing()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();

        var resolution = await resolver.ResolveProjectAsync(
            CompanionSeeder.DemoUserId, "did my tax refund spreadsheet come through?");

        Assert.Null(resolution.Best);
        Assert.False(resolution.RequiresClarification);
    }

    [Fact]
    public async Task Resolution_IsScopedByUser()
    {
        await using var host = await SeededHostAsync();
        using var scope = host.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();

        // A different user has no projects, so nothing resolves.
        var resolution = await resolver.ResolveProjectAsync("stranger", "how is the buoy going?");

        Assert.Empty(resolution.Candidates);
        Assert.Null(resolution.Best);
    }
}
