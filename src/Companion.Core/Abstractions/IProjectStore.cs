using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists and reads first-class project state: projects, aliases, activity events,
/// decisions, and open loops. All operations are user-scoped.
/// </summary>
public interface IProjectStore
{
    // Projects & aliases
    Task AddProjectAsync(Project project, CancellationToken ct = default);
    Task UpdateProjectAsync(Project project, CancellationToken ct = default);
    Task<Project?> GetProjectAsync(Guid id, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(string userId, CancellationToken ct = default);
    Task AddAliasAsync(ProjectAlias alias, CancellationToken ct = default);
    Task UpdateAliasAsync(ProjectAlias alias, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectAlias>> GetAliasesAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectAlias>> GetAliasesByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task DeleteProjectAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Moves all aliases, events, decisions, and open loops from one project to another.</summary>
    Task ReassignChildrenAsync(Guid fromProjectId, Guid toProjectId, string userId, CancellationToken ct = default);

    // Activity log & decisions
    Task AddEventAsync(ProjectEvent projectEvent, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectEvent>> GetRecentEventsAsync(Guid projectId, int count, CancellationToken ct = default);
    Task AddDecisionAsync(Decision decision, CancellationToken ct = default);
    Task<IReadOnlyList<Decision>> GetDecisionsAsync(Guid projectId, CancellationToken ct = default);

    // Open loops
    Task AddOpenLoopAsync(OpenLoop openLoop, CancellationToken ct = default);
    Task UpdateOpenLoopAsync(OpenLoop openLoop, CancellationToken ct = default);
    Task<OpenLoop?> GetOpenLoopAsync(Guid id, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<OpenLoop>> GetOpenLoopsAsync(string userId, bool onlyOpen, CancellationToken ct = default);
    Task<IReadOnlyList<OpenLoop>> GetOpenLoopsByProjectAsync(Guid projectId, bool onlyOpen, CancellationToken ct = default);
}
