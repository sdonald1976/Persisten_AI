using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// Reflects accepted memories into project state:
///   - a newly-accepted planned/in-progress episode opens an open loop;
///   - an episode reported as resolved closes the best-matching open loop;
///   - activity is logged against the resolved project.
/// Only accepted/merged episodic decisions are considered, and only real, evidence-backed
/// memories reach this point — extraction has already validated them.
/// </summary>
public sealed class ProjectUpdater : IProjectUpdater
{
    private const double ClosureSimilarityThreshold = 0.55;

    private readonly IProjectStore _projects;
    private readonly IEmbeddingModel _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectUpdater> _logger;

    public ProjectUpdater(
        IProjectStore projects,
        IEmbeddingModel embeddings,
        TimeProvider clock,
        ILogger<ProjectUpdater> logger)
    {
        _projects = projects;
        _embeddings = embeddings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProjectUpdateResult> ApplyAsync(
        string userId,
        IReadOnlyList<Message> exchange,
        MemoryExtractionResult extraction,
        ProjectContext projectContext,
        CancellationToken ct = default)
    {
        var project = projectContext.Resolution.Best?.Project;
        var actions = new List<string>();
        var now = _clock.GetUtcNow();

        var episodic = extraction.Decisions
            .Where(d => d.Candidate.Kind == MemoryKind.Episodic
                && d.Outcome is MemoryDecisionKind.Accepted or MemoryDecisionKind.Merged)
            .ToList();

        foreach (var decision in episodic)
        {
            var candidate = decision.Candidate;
            var sourceMessageId = candidate.Evidence.Count > 0 ? candidate.Evidence[0].MessageId : (Guid?)null;

            if (decision.Outcome == MemoryDecisionKind.Accepted &&
                candidate.EpisodeStatus is EpisodeStatus.Planned or EpisodeStatus.InProgress)
            {
                await OpenLoopAsync(userId, project, candidate, sourceMessageId, now, ct);
                actions.Add($"opened loop: {candidate.Content}");
            }
            else if (candidate.EpisodeStatus == EpisodeStatus.Resolved)
            {
                var closed = await TryCloseLoopAsync(userId, project, candidate, now, ct);
                if (closed is not null)
                    actions.Add($"closed loop: {closed}");
            }
        }

        // Log activity against the resolved project.
        if (project is not null && episodic.Count > 0)
        {
            project.LastActivityAt = now;
            await _projects.UpdateProjectAsync(project, ct);
        }

        if (actions.Count > 0)
            _logger.LogInformation("Project update for {UserId}: {Actions}", userId, string.Join("; ", actions));

        return new ProjectUpdateResult { Actions = actions };
    }

    private async Task OpenLoopAsync(
        string userId, Project? project, MemoryCandidate candidate, Guid? sourceMessageId,
        DateTimeOffset now, CancellationToken ct)
    {
        var loop = new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProjectId = project?.Id,
            Description = candidate.Content,
            Owner = "user",
            Status = OpenLoopStatus.Open,
            CreatedAt = now,
            SourceMessageId = sourceMessageId,
            Embedding = await _embeddings.EmbedAsync(candidate.Content, ct),
        };
        await _projects.AddOpenLoopAsync(loop, ct);

        if (project is not null)
            await LogEventAsync(userId, project.Id, ProjectEventKind.OpenLoopOpened,
                $"Opened: {candidate.Content}", sourceMessageId, now, ct);
    }

    private async Task<string?> TryCloseLoopAsync(
        string userId, Project? project, MemoryCandidate candidate, DateTimeOffset now, CancellationToken ct)
    {
        // Prefer loops in the resolved project; otherwise consider all the user's open loops.
        var loops = project is not null
            ? await _projects.GetOpenLoopsByProjectAsync(userId, project.Id, onlyOpen: true, ct)
            : await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (loops.Count == 0)
            return null;

        var embedding = await _embeddings.EmbedAsync(candidate.Content, ct);

        OpenLoop? best = null;
        var bestSim = 0.0;
        foreach (var loop in loops)
        {
            var sim = ScoreMath.Cosine(embedding, loop.Embedding);
            if (sim > bestSim)
            {
                bestSim = sim;
                best = loop;
            }
        }

        if (best is null || bestSim < ClosureSimilarityThreshold)
            return null;

        best.Status = OpenLoopStatus.Resolved;
        best.ClosedAt = now;
        best.ClosureEvidence = candidate.Content;
        await _projects.UpdateOpenLoopAsync(best, ct);

        if (best.ProjectId is { } projectId)
            await LogEventAsync(userId, projectId, ProjectEventKind.OpenLoopResolved,
                $"Resolved: {best.Description}", null, now, ct);

        return best.Description;
    }

    private Task LogEventAsync(
        string userId, Guid projectId, ProjectEventKind kind, string description,
        Guid? sourceMessageId, DateTimeOffset now, CancellationToken ct)
        => _projects.AddEventAsync(new ProjectEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = userId,
            Kind = kind,
            Description = description,
            Timestamp = now,
            SourceMessageId = sourceMessageId,
        }, ct);
}
