using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ProjectLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(TestHost host, Guid conversationId)> SeededSessionAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");
        return (host, conv.Id);
    }

    [Fact]
    public async Task PlannedMention_OpensAnOpenLoop()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        using (var scope = host.CreateScope())
        {
            var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();
            await companion.RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I plan to order a lidar sensor next week.");
        }

        using (var verify = host.CreateScope())
        {
            var loops = await verify.ServiceProvider.GetRequiredService<IProjectStore>()
                .GetOpenLoopsAsync(CompanionSeeder.DemoUserId, onlyOpen: true);
            Assert.Contains(loops, l => l.Description.Contains("lidar", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ResolvedMention_ClosesMatchingOpenLoop()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        Guid jetsonId;
        using (var scope = host.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectStore>();
            jetsonId = (await projects.GetProjectsAsync(CompanionSeeder.DemoUserId))
                .Single(p => p.Name == CompanionSeeder.JetsonProject).Id;

            // Precondition: the Jetson "test at home" loop is open.
            var openBefore = await projects.GetOpenLoopsByProjectAsync(jetsonId, onlyOpen: true);
            Assert.NotEmpty(openBefore);
        }

        using (var scope = host.CreateScope())
        {
            var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();
            await companion.RespondAsync(
                CompanionSeeder.DemoUserId, conversationId, "I tested the Jetson Nano deployment at home.");
        }

        using (var verify = host.CreateScope())
        {
            var projects = verify.ServiceProvider.GetRequiredService<IProjectStore>();
            var openAfter = await projects.GetOpenLoopsByProjectAsync(jetsonId, onlyOpen: true);
            Assert.Empty(openAfter); // the loop was closed

            var resolved = (await projects.GetOpenLoopsByProjectAsync(jetsonId, onlyOpen: false))
                .Single(l => l.Status == OpenLoopStatus.Resolved);
            Assert.NotNull(resolved.ClosureEvidence);
        }
    }
}
