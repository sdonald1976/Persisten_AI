using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Persistence;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// End-to-end tests for Priority 2: ambiguity is a control-flow state. The seed has two hardware
/// projects that both answer to "board" (Jetson + buoy), so "that board" is genuinely ambiguous.
/// </summary>
public class AmbiguityControlFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<Guid> SeedAndStartAsync(IServiceProvider sp)
    {
        await sp.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conv = await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        return conv.Id;
    }

    private static async Task<int> MemoryCountAsync(IServiceProvider sp)
        => (await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User)).Count;

    [Fact]
    public async Task AmbiguousReference_AsksAndRunsNothingElse()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await SeedAndStartAsync(sp);

        var before = await MemoryCountAsync(sp);
        var buoyLoopsOpenBefore = (await sp.GetRequiredService<IProjectStore>()
            .GetOpenLoopsAsync(User, onlyOpen: true)).Count;

        var companion = sp.GetRequiredService<ICompanion>();
        var trace = await companion.RespondAsync(User, conv, "That board finally arrived.");

        // Asked for clarification instead of answering.
        Assert.Equal(TurnStatus.ClarificationRequested, trace.Status);
        Assert.Contains("?", trace.Response);
        Assert.Empty(trace.Retrieved);                 // no retrieval-for-answer
        Assert.Equal(0, trace.Extraction.Accepted + trace.Extraction.Merged); // no memory extraction
        Assert.Empty(trace.ProjectUpdates.Actions);    // no project/open-loop mutation

        // A pending clarification was created and is persisted.
        var pending = await sp.GetRequiredService<IPendingClarificationStore>().GetActiveAsync(User, conv);
        Assert.NotNull(pending);
        Assert.Equal(ClarificationStatus.Pending, pending!.Status);

        // Nothing durable changed: no new memories, no open loop closed.
        Assert.Equal(before, await MemoryCountAsync(sp));
        Assert.Equal(buoyLoopsOpenBefore, (await sp.GetRequiredService<IProjectStore>()
            .GetOpenLoopsAsync(User, onlyOpen: true)).Count);
    }

    [Fact]
    public async Task Resolution_SelectsProject_ResumesOriginal_AndLeavesNoJunkMemory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await SeedAndStartAsync(sp);
        var companion = sp.GetRequiredService<ICompanion>();

        await companion.RespondAsync(User, conv, "That board finally arrived.");
        var trace = await companion.RespondAsync(User, conv, "The buoy one.");

        // Resolved and resumed the original request against the buoy project.
        Assert.Equal(TurnStatus.ClarificationResolved, trace.Status);
        Assert.Equal(CompanionSeeder.BuoyProject, trace.DetectedProject);

        // The pending is gone (resolved), with an audit trail explaining the resolution.
        Assert.Null(await sp.GetRequiredService<IPendingClarificationStore>().GetActiveAsync(User, conv));
        var resolved = await sp.GetRequiredService<CompanionDbContext>().PendingClarifications
            .SingleAsync(p => p.ConversationId == conv);
        Assert.Equal(ClarificationStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedProjectId);
        Assert.False(string.IsNullOrWhiteSpace(resolved.ResolutionNote));

        // "The buoy one." must not have become a durable memory.
        var memories = await sp.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);
        Assert.DoesNotContain(memories, m => m.Content.Contains("buoy one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnresolvedReply_AsksAgain_RatherThanGuessing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await SeedAndStartAsync(sp);
        var companion = sp.GetRequiredService<ICompanion>();

        await companion.RespondAsync(User, conv, "That board finally arrived.");
        var trace = await companion.RespondAsync(User, conv, "hmm, I'm honestly not sure");

        Assert.Equal(TurnStatus.ClarificationRequested, trace.Status);
        Assert.NotNull(await sp.GetRequiredService<IPendingClarificationStore>().GetActiveAsync(User, conv)); // still pending
    }

    [Fact]
    public async Task NeverMind_CancelsThePendingClarification()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = await SeedAndStartAsync(sp);
        var companion = sp.GetRequiredService<ICompanion>();

        await companion.RespondAsync(User, conv, "That board finally arrived.");
        var trace = await companion.RespondAsync(User, conv, "Never mind.");

        Assert.Equal(TurnStatus.ClarificationCancelled, trace.Status);
        Assert.Null(await sp.GetRequiredService<IPendingClarificationStore>().GetActiveAsync(User, conv)); // no longer pending
    }

    [Fact]
    public async Task PendingClarification_IsPersisted_AndResolvableFromAFreshScope()
    {
        await using var host = new TestHost(Now);

        Guid conv;
        using (var scope = host.CreateScope())
        {
            conv = await SeedAndStartAsync(scope.ServiceProvider);
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conv, "That board finally arrived.");
        }

        // A brand-new scope (simulating a restart against the same persisted store) still sees it,
        // and can resolve it — proving the pending item lives in the database, not in memory.
        using (var fresh = host.CreateScope())
        {
            var sp = fresh.ServiceProvider;
            Assert.NotNull(await sp.GetRequiredService<IPendingClarificationStore>().GetActiveAsync(User, conv));

            var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "the buoy project");
            Assert.Equal(TurnStatus.ClarificationResolved, trace.Status);
            Assert.Equal(CompanionSeeder.BuoyProject, trace.DetectedProject);
        }
    }
}
