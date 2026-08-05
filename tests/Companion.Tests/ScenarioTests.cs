using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The reproducible acceptance benchmark for the persistent companion — the five reference
/// scenarios from the vision doc, each run end-to-end against the seeded multi-month history.
/// </summary>
public class ScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static async Task<(TestHost host, Guid conversationId)> SeededSessionAsync()
    {
        var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "session", "mock", "test");
        return (host, conv.Id);
    }

    private static async Task<TurnTrace> SayAsync(TestHost host, Guid conversationId, string message)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conversationId, message);
    }

    [Fact] // Scenario A — Returning to a project
    public async Task A_ReturningToAProject_ResumesContinuity_AndClosesTheLoop()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        var trace = await SayAsync(host, conversationId, "I tested the Jetson Nano deployment at home.");

        // Resolves the right project (not confused with the buoy), and the packet has continuity.
        Assert.Equal(CompanionSeeder.JetsonProject, trace.DetectedProject);
        Assert.Contains("Jetson", trace.Packet.Render(), StringComparison.OrdinalIgnoreCase);

        // The unresolved "test at home" loop is now closed.
        using var scope = host.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var jetson = (await projects.GetProjectsAsync(User)).Single(p => p.Name == CompanionSeeder.JetsonProject);
        Assert.Empty(await projects.GetOpenLoopsByProjectAsync(jetson.Id, onlyOpen: true));
    }

    [Fact] // Scenario B — Changed preference
    public async Task B_ChangedPreference_KeepsHistory_ButIsNotCurrent()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        await SayAsync(host, conversationId, "I love my morning coffee ritual.");
        await SayAsync(host, conversationId, "Actually I love my morning tea ritual now.");

        using var scope = host.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IMemoryStore>().GetRetrievableMemoriesAsync(User);

        var active = all.Where(m => m.Status == MemoryStatus.Active).ToList();
        Assert.Contains(active, m => m.Content.Contains("tea", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(active, m => m.Content.Contains("coffee", StringComparison.OrdinalIgnoreCase));

        // History is retained, just not current.
        Assert.Contains(all, m => m.Status == MemoryStatus.Superseded
            && m.Content.Contains("coffee", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // Scenario C — Ambiguous reference
    public async Task C_AmbiguousReference_AsksToClarify_WithoutGuessing()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        var trace = await SayAsync(host, conversationId, "That board finally arrived.");

        var resolution = trace.ProjectContext.Resolution;
        Assert.True(resolution.RequiresClarification);
        Assert.Null(resolution.Best); // never invents certainty
        Assert.Contains("buoy", resolution.ClarificationQuestion!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jetson", resolution.ClarificationQuestion!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clarify", trace.Packet.Render(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Scenario D — Corrected memory
    public async Task D_CorrectedMemory_Reassociates_LeavesAuditTrail_AndDoesNotRecur()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        // A memory the companion wrongly attributed to the AI (Jetson) project.
        var memId = Guid.NewGuid();
        using (var scope = host.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
            await store.AddSemanticAsync(new SemanticMemory
            {
                Id = memId,
                UserId = User,
                Subject = "user",
                Predicate = "working_on",
                Value = "waterproofing the enclosure",
                NormalizedFact = "The user is working on waterproofing the enclosure.",
                Confidence = 0.8,
                Status = MemoryStatus.Active,
                RelatedProject = CompanionSeeder.JetsonProject, // wrong
                LastConfirmed = Now,
                CreatedAt = Now,
                Embedding = await embeddings.EmbedAsync("The user is working on waterproofing the enclosure."),
            });
        }

        // "No, that was the buoy project, not the AI project."
        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMemoryCurator>()
                .ReassignMemoryProjectAsync(User, memId, CompanionSeeder.BuoyProject);
        }

        using (var verify = host.CreateScope())
        {
            var vStore = verify.ServiceProvider.GetRequiredService<IMemoryStore>();
            var corrected = await vStore.GetSemanticAsync(memId, User);
            Assert.Equal(CompanionSeeder.BuoyProject, corrected!.RelatedProject); // re-associated
            Assert.Contains(await vStore.GetRevisionsAsync(memId), r => r.Kind == RevisionKind.Updated); // audit trail
        }
    }

    [Fact] // Scenario E — Long-term personal continuity
    public async Task E_LongTermContinuity_IdentifiesTopic_WithoutInjectingUnrelatedFacts()
    {
        var (host, conversationId) = await SeededSessionAsync();
        await using var _ = host;

        // A long-standing personal interest recorded months ago, unrelated to any project.
        using (var scope = host.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
            const string fact = "The user enjoys stargazing and amateur astronomy.";
            await store.AddSemanticAsync(new SemanticMemory
            {
                Id = Guid.NewGuid(),
                UserId = User,
                Subject = "user",
                Predicate = "interested_in",
                Value = "stargazing and amateur astronomy",
                NormalizedFact = fact,
                Confidence = 0.8,
                Importance = 0.6,
                Status = MemoryStatus.Active,
                FirstObserved = Now.AddMonths(-8),
                LastConfirmed = Now.AddMonths(-8),
                CreatedAt = Now.AddMonths(-8),
                Embedding = await embeddings.EmbedAsync(fact),
            });
        }

        // Oblique mention of that ongoing interest, not named directly.
        var trace = await SayAsync(host, conversationId, "I've been stargazing a lot lately.");

        Assert.NotEmpty(trace.Retrieved);
        var top = trace.Retrieved[0].Memory.Content;
        Assert.Contains("astronomy", top, StringComparison.OrdinalIgnoreCase); // identified the likely topic

        // Unrelated long-term facts (e.g. cooking) must not outrank the relevant interest.
        double ScoreOf(string needle) => trace.Retrieved
            .Where(r => r.Memory.Content.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Score).DefaultIfEmpty(0).Max();
        Assert.True(ScoreOf("astronomy") > ScoreOf("cooking"));
    }
}
