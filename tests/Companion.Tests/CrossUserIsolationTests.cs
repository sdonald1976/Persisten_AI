using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Persistence-level user-isolation guarantees (Priority 1 hardening). These assert ownership is
/// enforced in the store queries themselves — possession of a memory/project/evidence id owned by
/// another user reveals nothing and mutates nothing — not merely in the CLI/agent above them.
/// </summary>
public class CrossUserIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private sealed record Seeded(Guid MemoryId, Guid ProjectId, Guid OpenLoopId);

    /// <summary>Seeds one user with a memory (+evidence +revision), a project, and an open loop.</summary>
    private static async Task<Seeded> SeedUserAsync(IServiceProvider sp, string userId)
    {
        var embeddings = sp.GetRequiredService<IEmbeddingModel>();
        var memories = sp.GetRequiredService<IMemoryStore>();
        var projects = sp.GetRequiredService<IProjectStore>();
        var conversations = sp.GetRequiredService<IConversationStore>();

        var conv = await conversations.StartConversationAsync(userId, "t", "mock", "test");
        var msg = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = userId,
            Role = MessageRole.User, Content = $"{userId} secret note", Timestamp = Now,
        };
        await conversations.AddMessageAsync(msg);

        var memId = Guid.NewGuid();
        var fact = $"{userId} keeps a private fact";
        var mem = new SemanticMemory
        {
            Id = memId, UserId = userId, Subject = "user", Predicate = "note", Value = fact,
            NormalizedFact = fact, Confidence = 0.9, Status = MemoryStatus.Active,
            LastConfirmed = Now, CreatedAt = Now, Embedding = await embeddings.EmbedAsync(fact),
            Evidence = new List<MemoryEvidence> { new() { MessageId = msg.Id, Excerpt = "secret note", Weight = 1.0 } },
        };
        await memories.AddSemanticAsync(mem);
        await memories.AddRevisionAsync(userId, new MemoryRevision
        {
            Id = Guid.NewGuid(), MemoryId = memId, MemoryKind = MemoryKind.Semantic,
            Kind = RevisionKind.Created, Timestamp = Now, Actor = "test", Note = "seeded",
        });

        var projId = Guid.NewGuid();
        await projects.AddProjectAsync(new Project
        {
            Id = projId, UserId = userId, Name = $"{userId}-project",
            Status = ProjectStatus.Active, LastActivityAt = Now,
            Embedding = await embeddings.EmbedAsync($"{userId}-project"),
        });
        await projects.AddAliasAsync(new ProjectAlias { Id = Guid.NewGuid(), UserId = userId, ProjectId = projId, Alias = "the thing" });
        await projects.AddDecisionAsync(new Decision { Id = Guid.NewGuid(), UserId = userId, ProjectId = projId, Statement = "chose X", DecidedAt = Now });
        await projects.AddEventAsync(new ProjectEvent { Id = Guid.NewGuid(), UserId = userId, ProjectId = projId, Description = "did Y", Timestamp = Now });

        var loopId = Guid.NewGuid();
        await projects.AddOpenLoopAsync(new OpenLoop
        {
            Id = loopId, UserId = userId, ProjectId = projId, Description = $"{userId} unfinished task",
            Status = OpenLoopStatus.Open, CreatedAt = Now, Embedding = await embeddings.EmbedAsync("unfinished task"),
        });

        return new Seeded(memId, projId, loopId);
    }

    // ---- reads: a foreign id reveals nothing ----

    [Fact]
    public async Task UserA_CannotReadUserBs_Evidence()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        await SeedUserAsync(sp, UserA);
        var memories = sp.GetRequiredService<IMemoryStore>();

        Assert.Empty(await memories.GetEvidenceAsync(UserA, b.MemoryId));   // foreign id → nothing
        Assert.NotEmpty(await memories.GetEvidenceAsync(UserB, b.MemoryId)); // owner still works
    }

    [Fact]
    public async Task UserA_CannotReadUserBs_Revisions()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var memories = sp.GetRequiredService<IMemoryStore>();

        Assert.Empty(await memories.GetRevisionsAsync(UserA, b.MemoryId));
        Assert.NotEmpty(await memories.GetRevisionsAsync(UserB, b.MemoryId));
    }

    [Fact]
    public async Task UserA_CannotRetrieveUserBs_ProjectById()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var projects = sp.GetRequiredService<IProjectStore>();

        Assert.Null(await projects.GetProjectAsync(b.ProjectId, UserA));
        Assert.NotNull(await projects.GetProjectAsync(b.ProjectId, UserB));
    }

    [Fact]
    public async Task UserA_CannotReadUserBs_ProjectChildren()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var projects = sp.GetRequiredService<IProjectStore>();

        Assert.Empty(await projects.GetOpenLoopsByProjectAsync(UserA, b.ProjectId, onlyOpen: false));
        Assert.Empty(await projects.GetDecisionsAsync(UserA, b.ProjectId));
        Assert.Empty(await projects.GetRecentEventsAsync(UserA, b.ProjectId, 10));
        Assert.Empty(await projects.GetAliasesByProjectAsync(UserA, b.ProjectId));

        // Owner sees them all.
        Assert.NotEmpty(await projects.GetOpenLoopsByProjectAsync(UserB, b.ProjectId, onlyOpen: false));
        Assert.NotEmpty(await projects.GetDecisionsAsync(UserB, b.ProjectId));
        Assert.NotEmpty(await projects.GetRecentEventsAsync(UserB, b.ProjectId, 10));
        Assert.NotEmpty(await projects.GetAliasesByProjectAsync(UserB, b.ProjectId));
    }

    [Fact]
    public async Task UserA_CannotReadUserBs_OpenLoopById()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var projects = sp.GetRequiredService<IProjectStore>();

        Assert.Null(await projects.GetOpenLoopAsync(b.OpenLoopId, UserA));
        Assert.NotNull(await projects.GetOpenLoopAsync(b.OpenLoopId, UserB));
    }

    // ---- mutations: a foreign id changes nothing ----

    [Fact]
    public async Task UserA_CannotMutateUserBs_Memory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var curator = sp.GetRequiredService<IMemoryCurator>();

        // Every curation op must refuse a memory it doesn't own.
        Assert.False(await curator.ForgetAsync(UserA, b.MemoryId, "nope"));
        Assert.False(await curator.DisputeAsync(UserA, b.MemoryId, "nope"));
        Assert.False(await curator.CorrectSemanticAsync(UserA, b.MemoryId, "hacked", "hacked"));
        Assert.False(await curator.ReassignMemoryProjectAsync(UserA, b.MemoryId, "evil-project"));
        Assert.False(await curator.MergeAsync(UserA, b.MemoryId, b.MemoryId));

        // Untouched: still active for its real owner.
        var mem = await sp.GetRequiredService<IMemoryStore>().GetSemanticAsync(b.MemoryId, UserB);
        Assert.NotNull(mem);
        Assert.Equal(MemoryStatus.Active, mem!.Status);
    }

    [Fact]
    public async Task Owner_CanStillMutateOwnMemory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b = await SeedUserAsync(sp, UserB);
        var curator = sp.GetRequiredService<IMemoryCurator>();

        Assert.True(await curator.DisputeAsync(UserB, b.MemoryId, "on reflection"));
        var mem = await sp.GetRequiredService<IMemoryStore>().GetSemanticAsync(b.MemoryId, UserB);
        Assert.Equal(MemoryStatus.Disputed, mem!.Status);
    }

    [Fact]
    public async Task UserA_CannotMergeUserBs_Projects()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var b1 = await SeedUserAsync(sp, UserB);
        // A second project for B to merge into.
        var projects = sp.GetRequiredService<IProjectStore>();
        var target = Guid.NewGuid();
        await projects.AddProjectAsync(new Project { Id = target, UserId = UserB, Name = "b-target", Status = ProjectStatus.Active, LastActivityAt = Now });

        var curator = sp.GetRequiredService<IProjectCurator>();
        Assert.False(await curator.MergeProjectsAsync(UserA, b1.ProjectId, target));
        // Both of B's projects remain.
        Assert.NotNull(await projects.GetProjectAsync(b1.ProjectId, UserB));
        Assert.NotNull(await projects.GetProjectAsync(target, UserB));
    }
}
