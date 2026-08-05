using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed store for first-class project state. Every query is user-scoped.</summary>
public sealed class ProjectStore : IProjectStore
{
    private readonly CompanionDbContext _db;

    public ProjectStore(CompanionDbContext db) => _db = db;

    public async Task AddProjectAsync(Project project, CancellationToken ct = default)
    {
        _db.Projects.Add(project);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateProjectAsync(Project project, CancellationToken ct = default)
    {
        _db.Projects.Update(project);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Project?> GetProjectAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(string userId, CancellationToken ct = default)
        => await _db.Projects.Where(p => p.UserId == userId).ToListAsync(ct);

    public async Task AddAliasAsync(ProjectAlias alias, CancellationToken ct = default)
    {
        _db.ProjectAliases.Add(alias);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAliasAsync(ProjectAlias alias, CancellationToken ct = default)
    {
        _db.ProjectAliases.Update(alias);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectAlias>> GetAliasesAsync(string userId, CancellationToken ct = default)
        => await _db.ProjectAliases.Where(a => a.UserId == userId).ToListAsync(ct);

    public async Task<IReadOnlyList<ProjectAlias>> GetAliasesByProjectAsync(
        Guid projectId, CancellationToken ct = default)
        => await _db.ProjectAliases.Where(a => a.ProjectId == projectId).ToListAsync(ct);

    public async Task DeleteProjectAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (project is null)
            return;
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReassignChildrenAsync(
        Guid fromProjectId, Guid toProjectId, string userId, CancellationToken ct = default)
    {
        var aliases = await _db.ProjectAliases.Where(a => a.ProjectId == fromProjectId && a.UserId == userId).ToListAsync(ct);
        foreach (var a in aliases) a.ProjectId = toProjectId;

        var events = await _db.ProjectEvents.Where(e => e.ProjectId == fromProjectId && e.UserId == userId).ToListAsync(ct);
        foreach (var e in events) e.ProjectId = toProjectId;

        var decisions = await _db.Decisions.Where(d => d.ProjectId == fromProjectId && d.UserId == userId).ToListAsync(ct);
        foreach (var d in decisions) d.ProjectId = toProjectId;

        var loops = await _db.OpenLoops.Where(l => l.ProjectId == fromProjectId && l.UserId == userId).ToListAsync(ct);
        foreach (var l in loops) l.ProjectId = toProjectId;

        await _db.SaveChangesAsync(ct);
    }

    public async Task AddEventAsync(ProjectEvent projectEvent, CancellationToken ct = default)
    {
        _db.ProjectEvents.Add(projectEvent);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectEvent>> GetRecentEventsAsync(
        Guid projectId, int count, CancellationToken ct = default)
    {
        var recent = await _db.ProjectEvents
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToListAsync(ct);
        recent.Reverse(); // oldest-first
        return recent;
    }

    public async Task AddDecisionAsync(Decision decision, CancellationToken ct = default)
    {
        _db.Decisions.Add(decision);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Decision>> GetDecisionsAsync(Guid projectId, CancellationToken ct = default)
        => await _db.Decisions
            .Where(d => d.ProjectId == projectId && d.Status != MemoryStatus.Deleted)
            .OrderBy(d => d.DecidedAt)
            .ToListAsync(ct);

    public async Task AddOpenLoopAsync(OpenLoop openLoop, CancellationToken ct = default)
    {
        _db.OpenLoops.Add(openLoop);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateOpenLoopAsync(OpenLoop openLoop, CancellationToken ct = default)
    {
        _db.OpenLoops.Update(openLoop);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<OpenLoop?> GetOpenLoopAsync(Guid id, string userId, CancellationToken ct = default)
        => await _db.OpenLoops.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId, ct);

    public async Task<IReadOnlyList<OpenLoop>> GetOpenLoopsAsync(
        string userId, bool onlyOpen, CancellationToken ct = default)
        => await _db.OpenLoops
            .Where(l => l.UserId == userId && (!onlyOpen || l.Status == OpenLoopStatus.Open))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<OpenLoop>> GetOpenLoopsByProjectAsync(
        Guid projectId, bool onlyOpen, CancellationToken ct = default)
        => await _db.OpenLoops
            .Where(l => l.ProjectId == projectId && (!onlyOpen || l.Status == OpenLoopStatus.Open))
            .ToListAsync(ct);
}
