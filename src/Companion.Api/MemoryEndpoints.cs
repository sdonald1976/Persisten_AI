using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Api;

/// <summary>Structured read + curation endpoints over memories, projects and open loops.</summary>
internal static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        // ---- structured read endpoints (for rich UIs; the conversational path covers the same ground) ----

        app.MapGet("/memories", async (IUserContext user, IMemoryStore store, CancellationToken ct) =>
        {
            var memories = await store.GetRetrievalCandidatesAsync(user.UserId, ct);
            return Results.Ok(memories.OrderByDescending(m => m.EffectiveAt).Select(MemoryDto.From));
        });

        app.MapGet("/projects", async (IUserContext user, IProjectStore store, CancellationToken ct) =>
        {
            var projects = await store.GetProjectsAsync(user.UserId, ct);
            return Results.Ok(projects.OrderByDescending(p => p.LastActivityAt).Select(ProjectDto.From));
        });

        app.MapGet("/projects/{name}", async (string name, IUserContext user,
            IEntityResolver resolver, IProjectContextService context, CancellationToken ct) =>
        {
            var uid = user.UserId;
            var resolution = await resolver.ResolveProjectAsync(uid, name, ct);
            if (resolution.RequiresClarification)
                return Results.Ok(new { requiresClarification = true, question = resolution.ClarificationQuestion });
            if (resolution.Best is null)
                return Results.NotFound(new { error = $"No project matching \"{name}\"." });

            var summary = await context.GetSummaryAsync(uid, resolution.Best.Project.Id, ct);
            if (summary is null)
                return Results.NotFound(new { error = "Project has no summary." });

            return Results.Ok(new
            {
                name = summary.Project.Name,
                status = summary.Project.Status.ToString(),
                purpose = summary.Project.Purpose,
                decisions = summary.Decisions.Select(d => d.Statement),
                openLoops = summary.OpenLoops.Select(l => l.Description),
                recentActivity = summary.RecentEvents.Select(e => e.Description),
            });
        });

        // ---- project curation: the human-driven corrections the resolver can't make alone ----

        // Fold one project into another (aliases, events, decisions, loops, and memory references all
        // move to the survivor; the old name lives on as an alias so references still resolve).
        app.MapPost("/projects/merge", async (
            MergeProjectsRequest req, IUserContext user, IProjectCurator curator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(req.SourceId, out var sourceId) || !Guid.TryParse(req.TargetId, out var targetId))
                return Results.BadRequest(new { error = "SourceId and TargetId must be project ids (see GET /projects)." });
            if (sourceId == targetId)
                return Results.BadRequest(new { error = "A project can't be merged into itself." });

            var merged = await curator.MergeProjectsAsync(user.UserId, sourceId, targetId, ct);
            return merged
                ? Results.Ok(new { merged = true })
                : Results.NotFound(new { error = "One or both projects weren't found for this user." });
        });

        // Split a new project out of an existing one, optionally taking named loops/aliases with it.
        app.MapPost("/projects/split", async (
            SplitProjectRequest req, IUserContext user, IProjectCurator curator, CancellationToken ct) =>
        {
            if (!Guid.TryParse(req.SourceId, out var sourceId))
                return Results.BadRequest(new { error = "SourceId must be a project id (see GET /projects)." });
            if (string.IsNullOrWhiteSpace(req.NewName))
                return Results.BadRequest(new { error = "NewName is required." });

            var loopIds = (req.LoopIds ?? Array.Empty<string>())
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
            try
            {
                var created = await curator.SplitProjectAsync(
                    user.UserId, sourceId, req.NewName.Trim(), loopIds, req.Aliases ?? Array.Empty<string>(), ct);
                return Results.Ok(ProjectDto.From(created));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "The source project wasn't found for this user." });
            }
        });

        app.MapGet("/loops", async (IUserContext user, IProjectStore store, CancellationToken ct) =>
        {
            var loops = await store.GetOpenLoopsAsync(user.UserId, onlyOpen: true, ct);
            return Results.Ok(loops.Select(OpenLoopDto.From));
        });

    }
}
