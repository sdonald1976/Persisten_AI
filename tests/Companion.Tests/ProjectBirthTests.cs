using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The project birth path: an accepted memory naming a project the user doesn't have yet must
/// CREATE it (with a Created event and the turn's loops attached), an existing project must be
/// linked instead of duplicated, and junk/rejected/ambiguous names must create nothing.
/// </summary>
public class ProjectBirthTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "birth-user";

    private static MemoryDecision Decision(
        MemoryCandidate candidate, MemoryDecisionKind outcome = MemoryDecisionKind.Accepted) =>
        new() { Candidate = candidate, Outcome = outcome, Reason = "test", FinalConfidence = 0.8 };

    private static MemoryCandidate Episode(
        string content, string? project, EpisodeStatus status = EpisodeStatus.Planned) => new()
        {
            Kind = MemoryKind.Episodic,
            Content = content,
            EpisodeStatus = status,
            RelatedProject = project,
            Evidence = new[] { new CandidateEvidence(Guid.NewGuid(), content) },
        };

    private static MemoryExtractionResult Extraction(params MemoryDecision[] decisions) =>
        new() { Decisions = decisions };

    private static Task<ProjectUpdateResult> Apply(
        IServiceScope scope, MemoryExtractionResult extraction) =>
        scope.ServiceProvider.GetRequiredService<IProjectUpdater>().ApplyAsync(
            User, Array.Empty<Message>(), extraction, ProjectContext.Empty);

    [Fact]
    public async Task NewProjectName_CreatesTheProject_WithEventAndAttachedLoop()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        var result = await Apply(scope, Extraction(Decision(
            Episode("Started wiring the greenhouse sensor board", "Greenhouse automation"))));

        Assert.Contains(result.Actions, a => a.Contains("created project: Greenhouse automation"));

        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var project = Assert.Single(await store.GetProjectsAsync(User));
        Assert.Equal("Greenhouse automation", project.Name);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(Now, project.CreatedAt);
        Assert.NotNull(project.Embedding);

        // The birth is evidenced, and the loop opened this same turn belongs to the newborn.
        var events = await store.GetRecentEventsAsync(User, project.Id, 10);
        Assert.Contains(events, e => e.Kind == ProjectEventKind.Created);
        Assert.Contains(events, e => e.Kind == ProjectEventKind.OpenLoopOpened);
        var loop = Assert.Single(await store.GetOpenLoopsByProjectAsync(User, project.Id, onlyOpen: true));
        Assert.Contains("greenhouse sensor board", loop.Description);
    }

    [Fact]
    public async Task ExistingProject_IsLinked_NotDuplicated()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
        await store.AddProjectAsync(new Project
        {
            Id = Guid.NewGuid(), UserId = User, Name = "Greenhouse automation",
            CreatedAt = Now.AddDays(-30), LastActivityAt = Now.AddDays(-3),
            Embedding = await embeddings.EmbedAsync("Greenhouse automation"),
        });

        var result = await Apply(scope, Extraction(Decision(
            Episode("Calibrated the greenhouse automation moisture sensors", "greenhouse automation"))));

        Assert.Single(await store.GetProjectsAsync(User));
        Assert.Contains(result.Actions, a => a.Contains("linked to existing project"));
    }

    [Fact]
    public async Task SecondTurn_ResolvesTheBornProject()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        await Apply(scope, Extraction(Decision(
            Episode("Started wiring the greenhouse sensor board", "Greenhouse automation"))));

        var resolution = await scope.ServiceProvider.GetRequiredService<IEntityResolver>()
            .ResolveProjectAsync(User, "how's the greenhouse project going?");

        Assert.NotNull(resolution.Best);
        Assert.Equal("Greenhouse automation", resolution.Best!.Project.Name);
    }

    [Fact]
    public async Task NoProjectName_OrRejectedCandidate_OrTinyName_CreatesNothing()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        await Apply(scope, Extraction(
            Decision(Episode("Went for a run this morning", project: null)),
            Decision(Episode("Started the mega build", "Mega build"), MemoryDecisionKind.Rejected),
            Decision(Episode("Tweaked it a bit", "it"))));

        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        Assert.Empty(await store.GetProjectsAsync(User));
    }

    [Fact]
    public async Task OnlyOneProject_PerTurn_TheMostMentionedName()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        await Apply(scope, Extraction(
            Decision(Episode("Sketched the birdhouse camera mount", "Birdhouse camera")),
            Decision(Episode("Ordered a wide lens for the birdhouse camera", "Birdhouse camera")),
            Decision(Episode("Also thought about the koi pond", "Koi pond monitor"))));

        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var project = Assert.Single(await store.GetProjectsAsync(User));
        Assert.Equal("Birdhouse camera", project.Name);
    }

    [Fact]
    public async Task ResolvedTurn_DoesNotBirthASecondProject()
    {
        // When the turn already resolved a project, a differently-named memory must not spawn
        // another one behind the user's back.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var existing = new Project
        {
            Id = Guid.NewGuid(), UserId = User, Name = "Jetson deployment",
            CreatedAt = Now.AddDays(-10), LastActivityAt = Now.AddDays(-1),
        };
        await store.AddProjectAsync(existing);

        var context = new ProjectContext
        {
            Resolution = new EntityResolution
            {
                Candidates = new[] { new ProjectMatch
                {
                    Project = existing, Score = 1, Confidence = 1, Reason = "test",
                    Signals = new Dictionary<string, double>(),
                } },
                Best = new ProjectMatch
                {
                    Project = existing, Score = 1, Confidence = 1, Reason = "test",
                    Signals = new Dictionary<string, double>(),
                },
                RequiresClarification = false,
            },
        };

        await scope.ServiceProvider.GetRequiredService<IProjectUpdater>().ApplyAsync(
            User, Array.Empty<Message>(),
            Extraction(Decision(Episode("Set up the aquarium lights", "Aquarium controller"))),
            context);

        Assert.Single(await store.GetProjectsAsync(User));
    }
}
