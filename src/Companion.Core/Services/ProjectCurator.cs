using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// Project-level corrections: merge two references that are really one project, and split a
/// project that wrongly absorbed things. Merges re-point every child (aliases, events,
/// decisions, open loops) and every memory that named the old project, then remove the
/// duplicate — leaving an activity-log trail on the surviving project.
/// </summary>
public sealed class ProjectCurator : IProjectCurator
{
    private readonly IProjectStore _projects;
    private readonly IMemoryStore _memories;
    private readonly IEmbeddingModel _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectCurator> _logger;

    public ProjectCurator(
        IProjectStore projects,
        IMemoryStore memories,
        IEmbeddingModel embeddings,
        TimeProvider clock,
        ILogger<ProjectCurator> logger)
    {
        _projects = projects;
        _memories = memories;
        _embeddings = embeddings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> MergeProjectsAsync(
        string userId, Guid sourceId, Guid targetId, CancellationToken ct = default)
    {
        if (sourceId == targetId)
            return false;

        var source = await _projects.GetProjectAsync(sourceId, userId, ct);
        var target = await _projects.GetProjectAsync(targetId, userId, ct);
        if (source is null || target is null)
            return false;

        var now = _clock.GetUtcNow();

        // Move all child records, then re-point memories that referenced the source by name.
        await _projects.ReassignChildrenAsync(sourceId, targetId, userId, ct);
        await _memories.ReassignProjectAsync(userId, source.Name, target.Name, ct);

        // Preserve the old name as an alias of the survivor so references still resolve.
        await _projects.AddAliasAsync(new ProjectAlias
        {
            Id = Guid.NewGuid(),
            ProjectId = targetId,
            UserId = userId,
            Alias = source.Name,
            Source = "merge",
            Confidence = 1.0,
        }, ct);

        await _projects.AddEventAsync(new ProjectEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = targetId,
            UserId = userId,
            Kind = ProjectEventKind.Note,
            Description = $"Merged \"{source.Name}\" into this project.",
            Timestamp = now,
        }, ct);

        target.LastActivityAt = now;
        await _projects.UpdateProjectAsync(target, ct);
        await _projects.DeleteProjectAsync(sourceId, userId, ct);

        _logger.LogInformation("Merged project {Source} into {Target} for {User}", sourceId, targetId, userId);
        return true;
    }

    public async Task<Project> SplitProjectAsync(
        string userId,
        Guid sourceId,
        string newProjectName,
        IReadOnlyList<Guid> openLoopIdsToMove,
        IReadOnlyList<string> aliasesToMove,
        CancellationToken ct = default)
    {
        var source = await _projects.GetProjectAsync(sourceId, userId, ct)
            ?? throw new InvalidOperationException($"Project {sourceId} not found for user.");

        var now = _clock.GetUtcNow();
        var created = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = newProjectName,
            Purpose = source.Purpose,
            Status = ProjectStatus.Active,
            CreatedAt = now,
            LastActivityAt = now,
            Embedding = await _embeddings.EmbedAsync(newProjectName, ct),
        };
        await _projects.AddProjectAsync(created, ct);

        foreach (var loopId in openLoopIdsToMove)
        {
            var loop = await _projects.GetOpenLoopAsync(loopId, userId, ct);
            if (loop is not null && loop.ProjectId == sourceId)
            {
                loop.ProjectId = created.Id;
                await _projects.UpdateOpenLoopAsync(loop, ct);
            }
        }

        var sourceAliases = await _projects.GetAliasesByProjectAsync(sourceId, ct);
        foreach (var alias in sourceAliases.Where(a => aliasesToMove.Contains(a.Alias, StringComparer.OrdinalIgnoreCase)))
        {
            alias.ProjectId = created.Id;
            await _projects.UpdateAliasAsync(alias, ct);
        }

        await _projects.AddEventAsync(new ProjectEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = created.Id,
            UserId = userId,
            Kind = ProjectEventKind.Created,
            Description = $"Split out from \"{source.Name}\".",
            Timestamp = now,
        }, ct);

        _logger.LogInformation("Split project {Source} → new {New} for {User}", sourceId, created.Id, userId);
        return created;
    }
}
