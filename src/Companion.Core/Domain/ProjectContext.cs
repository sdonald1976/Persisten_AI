namespace Companion.Core.Domain;

/// <summary>
/// The project-aware context for a turn: how the query's project reference resolved, the
/// reconstructed summary of the resolved project (if any), and the open loops relevant to the
/// turn. Assembled once and shared by the packet builder and diagnostics.
/// </summary>
public sealed record ProjectContext
{
    public required EntityResolution Resolution { get; init; }
    public ProjectSummary? Summary { get; init; }
    public IReadOnlyList<RetrievedOpenLoop> OpenLoops { get; init; } = Array.Empty<RetrievedOpenLoop>();

    /// <summary>The confidently-resolved project name, if any (convenience for retrieval boosting).</summary>
    public string? ResolvedProjectName => Resolution.Best?.Project.Name;

    public static readonly ProjectContext Empty = new() { Resolution = EntityResolution.None };
}

/// <summary>An open loop surfaced for a turn, with its relevance score and why it matched.</summary>
public sealed record RetrievedOpenLoop
{
    public required OpenLoop OpenLoop { get; init; }
    public required double Score { get; init; }
    public required string Reason { get; init; }
}
