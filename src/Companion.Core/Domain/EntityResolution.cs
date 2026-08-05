namespace Companion.Core.Domain;

/// <summary>
/// The result of resolving a user's reference (e.g. "that board") to a project. Evidence-based
/// and confidence-aware: it never silently merges uncertain candidates. When the top candidates
/// are too close to call, it asks for clarification instead of inventing certainty.
/// </summary>
public sealed record EntityResolution
{
    /// <summary>All plausible candidates, ranked most-likely first.</summary>
    public required IReadOnlyList<ProjectMatch> Candidates { get; init; }

    /// <summary>The chosen project, when confident enough to pick one.</summary>
    public ProjectMatch? Best { get; init; }

    /// <summary>True when the reference is ambiguous and a clarifying question is warranted.</summary>
    public bool RequiresClarification { get; init; }

    /// <summary>A concise question to disambiguate, when <see cref="RequiresClarification"/>.</summary>
    public string? ClarificationQuestion { get; init; }

    public static readonly EntityResolution None = new()
    {
        Candidates = Array.Empty<ProjectMatch>(),
        Best = null,
        RequiresClarification = false,
    };
}

/// <summary>One ranked candidate project for a reference, with its score breakdown and reason.</summary>
public sealed record ProjectMatch
{
    public required Project Project { get; init; }
    public required double Score { get; init; }
    public required double Confidence { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyDictionary<string, double> Signals { get; init; } = new Dictionary<string, double>();
}
