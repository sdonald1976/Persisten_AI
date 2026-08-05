using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Builds the project-aware context for a turn: resolve the query's project reference,
/// reconstruct the resolved project's summary, and surface relevant open loops.
/// </summary>
public interface IProjectContextService
{
    Task<ProjectContext> BuildAsync(string userId, string query, CancellationToken ct = default);

    /// <summary>Reconstructs a single project's current state (used by the CLI `/project` command).</summary>
    Task<ProjectSummary?> GetSummaryAsync(string userId, Guid projectId, CancellationToken ct = default);
}
