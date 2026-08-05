using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Corrections at the project level: merging two references that are really the same project,
/// and splitting one project that wrongly absorbed things that belong apart.
/// </summary>
public interface IProjectCurator
{
    /// <summary>
    /// Merges <paramref name="sourceId"/> into <paramref name="targetId"/>: reassigns aliases,
    /// events, decisions, and open loops; re-points memories referencing the source project name;
    /// records the source name as an alias of the target; then deletes the source project.
    /// </summary>
    Task<bool> MergeProjectsAsync(string userId, Guid sourceId, Guid targetId, CancellationToken ct = default);

    /// <summary>
    /// Splits part of a project into a new one: creates <paramref name="newProjectName"/> and moves
    /// the specified open loops and aliases to it. Used to undo an incorrect merge.
    /// </summary>
    Task<Project> SplitProjectAsync(
        string userId,
        Guid sourceId,
        string newProjectName,
        IReadOnlyList<Guid> openLoopIdsToMove,
        IReadOnlyList<string> aliasesToMove,
        CancellationToken ct = default);
}
