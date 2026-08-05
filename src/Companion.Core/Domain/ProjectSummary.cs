namespace Companion.Core.Domain;

/// <summary>
/// A reconstructed snapshot of a project's current state, assembled from its record plus its
/// decisions, open loops, and recent activity. This is what lets the companion pick a project
/// back up after months.
/// </summary>
public sealed record ProjectSummary
{
    public required Project Project { get; init; }
    public IReadOnlyList<OpenLoop> OpenLoops { get; init; } = Array.Empty<OpenLoop>();
    public IReadOnlyList<Decision> Decisions { get; init; } = Array.Empty<Decision>();
    public IReadOnlyList<ProjectEvent> RecentEvents { get; init; } = Array.Empty<ProjectEvent>();
}
