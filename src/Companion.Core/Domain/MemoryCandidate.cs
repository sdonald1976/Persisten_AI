namespace Companion.Core.Domain;

/// <summary>
/// A proposed memory produced by extraction, before any validation. Candidates are never
/// persisted as active memory directly — they flow through the pipeline, which decides
/// whether to accept, merge, reject, or defer them. Kind-specific fields are optional and
/// interpreted per <see cref="Kind"/>.
/// </summary>
public sealed record MemoryCandidate
{
    public required MemoryKind Kind { get; init; }

    /// <summary>The canonical text (normalized fact for semantic, description for episodic).</summary>
    public required string Content { get; init; }

    // --- semantic-only ---
    public string? Subject { get; init; }
    public string? Predicate { get; init; }
    public string? Value { get; init; }
    public Validity Validity { get; init; } = Validity.Current;

    // --- episodic-only ---
    public DateTimeOffset? EventTime { get; init; }
    public TimePrecision TimePrecision { get; init; } = TimePrecision.Approximate;
    public EpisodeStatus EpisodeStatus { get; init; } = EpisodeStatus.Occurred;

    // --- common ---
    public string? RelatedProject { get; init; }
    public double Importance { get; init; } = 0.5;

    /// <summary>The extractor's own confidence in this candidate (0..1); the pipeline recomputes a final value.</summary>
    public double ProposedConfidence { get; init; } = 0.5;

    /// <summary>
    /// The extractor's reading of whether the user is <em>changing</em> something they said before
    /// rather than adding to it. A proposal, never a decision: the pipeline still requires a
    /// plausible target and evidence from the user, and weighs this alongside its own reading of
    /// the user's wording. Two independent signals, because the cost of being wrong is a memory the
    /// user gave us and cannot see us lose.
    /// </summary>
    public bool ProposedReplacement { get; init; }

    /// <summary>Source messages this candidate is drawn from. Required for acceptance.</summary>
    public IReadOnlyList<CandidateEvidence> Evidence { get; init; } = Array.Empty<CandidateEvidence>();
}

/// <summary>A source reference for a candidate: which message, and the supporting excerpt.</summary>
public sealed record CandidateEvidence(Guid MessageId, string Excerpt);
