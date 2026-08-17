using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// Reflects accepted memories into project state:
///   - a memory naming a project the user doesn't have yet CREATES it (the birth path —
///     nothing else in the live pipeline ever creates a project);
///   - a newly-accepted planned/in-progress episode opens an open loop;
///   - an episode reported as resolved closes the best-matching open loop;
///   - activity is logged against the resolved project.
/// Only accepted/merged decisions are considered, and only real, evidence-backed memories
/// reach this point — extraction has already validated them.
/// </summary>
public sealed class ProjectUpdater : IProjectUpdater
{
    private const double ClosureSimilarityThreshold = 0.55;

    /// <summary>Shorter names ("app", "it") are reference noise, not a project identity.</summary>
    private const int MinProjectNameLength = 3;

    private readonly IProjectStore _projects;
    private readonly IEntityResolver _resolver;
    private readonly IEmbeddingModel _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectUpdater> _logger;

    public ProjectUpdater(
        IProjectStore projects,
        IEntityResolver resolver,
        IEmbeddingModel embeddings,
        TimeProvider clock,
        ILogger<ProjectUpdater> logger)
    {
        _projects = projects;
        _resolver = resolver;
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

        var accepted = extraction.Decisions
            .Where(d => d.Outcome is MemoryDecisionKind.Accepted or MemoryDecisionKind.Merged)
            .ToList();

        // 0. The birth path. Each project named by an accepted memory is either an existing
        // project the resolver can match (link it) or a genuinely new one (create it). Without
        // this, nothing in the live pipeline ever creates a project row — resolution can only find
        // what already exists.
        //
        // It runs for EVERY name in the turn, and whether or not a project already resolved. It
        // used to run only when nothing resolved, which meant that once a user had one project,
        // their next one could not be born: resolution kept matching the old project (loosely
        // enough that a soil-chemistry talk resolved to "the allotment"), so the birth path was
        // skipped every time. Three distinct projects mentioned across one conversation produced
        // one row, and with only ever one project there is also never anything to be ambiguous
        // between, so the clarifying-question path could not fire either.
        project = await EnsureProjectsAsync(userId, accepted, project, now, actions, ct);

        var episodic = accepted
            .Where(d => d.Candidate.Kind == MemoryKind.Episodic)
            .ToList();

        foreach (var decision in episodic)
        {
            var candidate = decision.Candidate;
            var sourceMessageId = candidate.Evidence.Count > 0 ? candidate.Evidence[0].MessageId : (Guid?)null;

            if (decision.Outcome == MemoryDecisionKind.Accepted &&
                candidate.EpisodeStatus is EpisodeStatus.Planned or EpisodeStatus.InProgress)
            {
                await OpenLoopAsync(userId, project, candidate.Content, sourceMessageId, now, ct);
                actions.Add($"opened loop: {candidate.Content}");
            }
            else if (candidate.EpisodeStatus == EpisodeStatus.Resolved)
            {
                var closed = await TryCloseLoopAsync(userId, project, candidate, now, ct);
                if (closed is not null)
                    actions.Add($"closed loop: {closed}");
            }
        }

        // Backstop: the extraction model routinely returns no episodic candidates at all, and when
        // it doesn't, nothing above runs and the user's unfinished work is silently dropped. Read
        // the obligation out of their own words instead.
        if (!episodic.Any(d => d.Candidate.EpisodeStatus is EpisodeStatus.Planned or EpisodeStatus.InProgress))
        {
            foreach (var message in exchange.Where(m => m.Role == MessageRole.User))
            {
                var outstanding = UnfinishedWorkDetector.Detect(message.Content);
                if (outstanding is null)
                    continue;

                var description = $"The user needs to {outstanding}.";
                if (await AlreadyOpenAsync(userId, description, ct))
                    continue;

                await OpenLoopAsync(userId, project, description, message.Id, now, ct);
                actions.Add($"opened loop: {description}");
                break;
            }
        }

        // Record explicit decisions the user voiced this turn ("we decided to use X") against
        // the project. Only with a project to hang them on — a decision with no context is a
        // fact for memory extraction, not project state.
        if (project is not null)
        {
            var recorded = await RecordDecisionsAsync(userId, project, exchange, now, ct);
            actions.AddRange(recorded.Select(d => $"recorded decision: {d}"));
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

    /// <summary>
    /// Makes sure the project this turn is <em>about</em> exists, and returns the one the rest of
    /// the turn should hang its work on. Conservative on purpose: never on an ambiguous resolution
    /// (two close existing candidates means ask, not multiply), and never from a name too short to
    /// be an identity.
    ///
    /// Still at most one project per turn, from the most-mentioned name: a passing "also thought
    /// about the koi pond" is not someone starting a koi pond project, and creating one from it
    /// would be exactly the "behind the user's back" birth this path has always refused.
    ///
    /// What has changed is that it no longer requires the turn to have resolved nothing. That
    /// condition made a second project impossible to start: resolution only ever matches projects
    /// that already exist, so a turn introducing a new one either resolves to an older project
    /// (loosely — a soil-chemistry talk resolved to "the allotment") or to nothing, and in the
    /// first case the birth path was skipped and the new project was simply lost. Three projects
    /// mentioned across one real conversation produced one row. The turn still hangs its loops and
    /// decisions on whatever it resolved; the newcomer is created, evidenced with a Created event,
    /// reported in <paramref name="actions"/>, and can be resolved to from the next turn onwards.
    /// </summary>
    private async Task<Project?> EnsureProjectsAsync(
        string userId, IReadOnlyList<MemoryDecision> accepted, Project? resolved,
        DateTimeOffset now, List<string> actions, CancellationToken ct)
    {
        var group = accepted
            .Where(d => !string.IsNullOrWhiteSpace(d.Candidate.RelatedProject)
                && d.Candidate.RelatedProject!.Trim().Length >= MinProjectNameLength)
            .GroupBy(d => d.Candidate.RelatedProject!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (group is null)
            return resolved;

        // Already the turn's project under a name the resolver would match — nothing to do.
        if (resolved is not null && group.Key.Equals(resolved.Name, StringComparison.OrdinalIgnoreCase))
            return resolved;

        var project = await ResolveOrCreateProjectAsync(userId, group, resolved, now, actions, ct);

        // What the turn's own reference resolved to stays the turn's project: resolution is the
        // authority on "which thing are we talking about", and a memory naming another one is not
        // a reason to move this turn's work over to it.
        return resolved ?? project;
    }

    private async Task<Project?> ResolveOrCreateProjectAsync(
        string userId, IGrouping<string, MemoryDecision> group, Project? resolved,
        DateTimeOffset now, List<string> actions, CancellationToken ct)
    {
        var name = group.Key;
        var evidence = group.First().Candidate.Evidence;
        var sourceMessageId = evidence.Count > 0 ? evidence[0].MessageId : (Guid?)null;

        // The extractor's name may still match an existing project the turn's own reference
        // didn't ("the detection thing" → memory names "Jetson Nano object detection").
        var resolution = await _resolver.ResolveProjectAsync(userId, name, ct);
        if (resolution.Best is not null)
        {
            var match = resolution.Best.Project;
            if (resolved is null || match.Id != resolved.Id)
                actions.Add($"linked to existing project: {match.Name}");
            return match;
        }
        if (resolution.RequiresClarification)
            return null; // two plausible existing projects — creating a third helps nobody

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Status = ProjectStatus.Active,
            CreatedAt = now,
            LastActivityAt = now,
            Embedding = await _embeddings.EmbedAsync(name, ct),
        };
        await _projects.AddProjectAsync(project, ct);
        await LogEventAsync(userId, project.Id, ProjectEventKind.Created,
            $"First mentioned: \"{group.First().Candidate.Content}\"", sourceMessageId, now, ct);

        actions.Add($"created project: {name}");
        _logger.LogInformation("Created project \"{Name}\" for {UserId} from an accepted memory.", name, userId);
        return project;
    }

    /// <summary>
    /// Detects and stores decisions voiced in the user's messages this turn, deduped against
    /// what's already recorded for the project (re-stating a decision isn't a new one).
    /// </summary>
    private async Task<List<string>> RecordDecisionsAsync(
        string userId, Project project, IReadOnlyList<Message> exchange,
        DateTimeOffset now, CancellationToken ct)
    {
        var recorded = new List<string>();
        var existing = (await _projects.GetDecisionsAsync(userId, project.Id, ct))
            .Select(d => d.Statement)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var message in exchange.Where(m => m.Role == MessageRole.User))
        {
            var statement = DecisionDetector.Detect(message.Content);
            if (statement is null || !existing.Add(statement))
                continue;

            await _projects.AddDecisionAsync(new Decision
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ProjectId = project.Id,
                Statement = statement,
                DecidedAt = now,
                SourceMessageId = message.Id,
            }, ct);
            await LogEventAsync(userId, project.Id, ProjectEventKind.DecisionMade,
                $"Decided: {statement}", message.Id, now, ct);
            recorded.Add(statement);
        }

        return recorded;
    }

    private async Task OpenLoopAsync(
        string userId, Project? project, string description, Guid? sourceMessageId,
        DateTimeOffset now, CancellationToken ct)
    {
        var loop = new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProjectId = project?.Id,
            Description = description,
            Owner = "user",
            Status = OpenLoopStatus.Open,
            CreatedAt = now,
            SourceMessageId = sourceMessageId,
            Embedding = await _embeddings.EmbedAsync(description, ct),
        };
        await _projects.AddOpenLoopAsync(loop, ct);

        if (project is not null)
            await LogEventAsync(userId, project.Id, ProjectEventKind.OpenLoopOpened,
                $"Opened: {description}", sourceMessageId, now, ct);
    }

    /// <summary>
    /// Whether the user already has an open loop about this. Someone mentioning the same
    /// outstanding job across several turns should not accumulate a loop per mention.
    /// </summary>
    private async Task<bool> AlreadyOpenAsync(string userId, string description, CancellationToken ct)
    {
        var loops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (loops.Count == 0)
            return false;

        var embedding = await _embeddings.EmbedAsync(description, ct);
        return loops.Any(l => ScoreMath.Cosine(embedding, l.Embedding) >= ClosureSimilarityThreshold);
    }

    private async Task<string?> TryCloseLoopAsync(
        string userId, Project? project, MemoryCandidate candidate, DateTimeOffset now, CancellationToken ct)
    {
        // Prefer loops in the resolved project; when it has none (e.g. a project born this very
        // turn, or loops opened before the project existed), fall back to all the user's open
        // loops — the similarity threshold below still guards against closing the wrong one.
        var loops = project is not null
            ? await _projects.GetOpenLoopsByProjectAsync(userId, project.Id, onlyOpen: true, ct)
            : await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (loops.Count == 0 && project is not null)
            loops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
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
