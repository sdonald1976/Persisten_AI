using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Builds the project-aware context for a turn: resolves the query's project reference,
/// reconstructs the resolved project's state, and surfaces the open loops most relevant to
/// the turn. Open loops of a confidently-resolved project are boosted so returning to a
/// project reliably resurfaces its unfinished business.
/// </summary>
public sealed class ProjectContextService : IProjectContextService
{
    private const double OpenLoopFloor = 0.1;
    private const int RecentEventCount = 10;

    private readonly IEntityResolver _resolver;
    private readonly IProjectStore _projects;
    private readonly IEmbeddingModel _embeddings;
    private readonly CompanionOptions _options;

    public ProjectContextService(
        IEntityResolver resolver,
        IProjectStore projects,
        IEmbeddingModel embeddings,
        IOptions<CompanionOptions> options)
    {
        _resolver = resolver;
        _projects = projects;
        _embeddings = embeddings;
        _options = options.Value;
    }

    public async Task<ProjectContext> BuildAsync(string userId, string query, CancellationToken ct = default)
    {
        var resolution = await _resolver.ResolveProjectAsync(userId, query, ct);

        var summary = resolution.Best is not null
            ? await BuildSummaryAsync(resolution.Best.Project, ct)
            : null;

        var openLoops = await RetrieveOpenLoopsAsync(userId, query, resolution.Best?.Project.Id, ct);

        return new ProjectContext
        {
            Resolution = resolution,
            Summary = summary,
            OpenLoops = openLoops,
        };
    }

    public async Task<ProjectContext> BuildForProjectAsync(
        string userId, string query, Guid projectId, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId, userId, ct);
        if (project is null)
            return await BuildAsync(userId, query, ct); // project vanished — fall back to normal resolution

        var match = new ProjectMatch
        {
            Project = project,
            Score = 1.0,
            Confidence = 1.0,
            Reason = "resolved via clarification",
        };
        return new ProjectContext
        {
            Resolution = new EntityResolution
            {
                Candidates = new[] { match },
                Best = match,
                RequiresClarification = false,
            },
            Summary = await BuildSummaryAsync(project, ct),
            OpenLoops = await RetrieveOpenLoopsAsync(userId, query, project.Id, ct),
        };
    }

    public async Task<ProjectSummary?> GetSummaryAsync(string userId, Guid projectId, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId, userId, ct);
        return project is null ? null : await BuildSummaryAsync(project, ct);
    }

    private async Task<ProjectSummary> BuildSummaryAsync(Project project, CancellationToken ct)
    {
        var openLoops = await _projects.GetOpenLoopsByProjectAsync(project.UserId, project.Id, onlyOpen: true, ct);
        var decisions = await _projects.GetDecisionsAsync(project.UserId, project.Id, ct);
        var events = await _projects.GetRecentEventsAsync(project.UserId, project.Id, RecentEventCount, ct);

        return new ProjectSummary
        {
            Project = project,
            OpenLoops = openLoops,
            Decisions = decisions,
            RecentEvents = events,
        };
    }

    private async Task<IReadOnlyList<RetrievedOpenLoop>> RetrieveOpenLoopsAsync(
        string userId, string query, Guid? resolvedProjectId, CancellationToken ct)
    {
        var loops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        if (loops.Count == 0)
            return Array.Empty<RetrievedOpenLoop>();

        // As in the retriever: an embedding outage degrades open-loop ranking to keywords and
        // project membership rather than failing the turn.
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddings.EmbedAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            queryEmbedding = Array.Empty<float>();
        }

        var scored = new List<RetrievedOpenLoop>(loops.Count);
        foreach (var loop in loops)
        {
            var emb = ScoreMath.Cosine(queryEmbedding, loop.Embedding);
            var kw = ScoreMath.KeywordOverlap(query, loop.Description);
            var projectMatch = resolvedProjectId is not null && loop.ProjectId == resolvedProjectId;

            var score = emb + kw * 0.6 + (projectMatch ? 0.6 : 0.0);
            if (score < OpenLoopFloor)
                continue;

            var reasons = new List<string>();
            if (projectMatch) reasons.Add("belongs to the active project");
            if (emb > 0.01) reasons.Add("semantically related");
            if (kw > 0.01) reasons.Add("shared keywords");

            scored.Add(new RetrievedOpenLoop
            {
                OpenLoop = loop,
                Score = score,
                Reason = reasons.Count == 0 ? "open loop" : string.Join("; ", reasons),
            });
        }

        return scored
            .OrderByDescending(r => r.Score)
            .Take(_options.MaxOpenLoops)
            .ToList();
    }
}
