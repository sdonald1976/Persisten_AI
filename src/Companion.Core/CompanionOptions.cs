namespace Companion.Core;

/// <summary>
/// Configuration-bound knobs for retrieval and context assembly. Bound from
/// the "Companion" section of appsettings.json.
/// </summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Companion";

    /// <summary>Max memories to include in the context packet after ranking.</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Approximate token budget for the memory section of the packet.</summary>
    public int MemoryTokenBudget { get; set; } = 800;

    /// <summary>How many recent messages to include verbatim.</summary>
    public int RecentMessageCount { get; set; } = 6;

    /// <summary>Recency half-life in days for the recency signal.</summary>
    public double RecencyHalfLifeDays { get; set; } = 45.0;

    /// <summary>Minimum combined score for a memory to be eligible for inclusion.</summary>
    public double MinScore { get; set; } = 0.05;

    /// <summary>
    /// Minimum topical relevance — raw semantic similarity + keyword overlap + project match — a
    /// memory must reach before it can enter the context packet. Recency, importance, and confidence
    /// rank the relevant memories but never admit one on their own; without this floor a recent or
    /// important fact scores above <see cref="MinScore"/> with zero relevance to the current turn,
    /// so unrelated things the companion "knows about the user" bleed into every reply. Also gates
    /// the open-loop boost (an unresolved item is only surfaced when the turn is already relevant to it).
    /// </summary>
    public double RelevanceFloor { get; set; } = 0.15;

    /// <summary>Per-signal weights for the hybrid retrieval score.</summary>
    public RetrievalWeights Weights { get; set; } = new();

    /// <summary>When true, each turn runs the extraction pipeline over the exchange.</summary>
    public bool EnableExtraction { get; set; } = true;

    /// <summary>Cosine similarity at/above which a candidate is treated as the same memory.</summary>
    public double DuplicateSimilarityThreshold { get; set; } = 0.82;

    /// <summary>Minimum final confidence for a brand-new memory to be accepted.</summary>
    public double MinAcceptConfidence { get; set; } = 0.35;

    /// <summary>
    /// Similarity at/above which a same-slot fact with a different value is treated as a
    /// change to the SAME topic (held for review) rather than an unrelated new fact. Below
    /// the duplicate threshold; above this, "user prefers X" vs "user prefers Y" only
    /// conflicts when X and Y are actually about the same thing.
    /// </summary>
    public double ContradictionSimilarityThreshold { get; set; } = 0.5;

    /// <summary>Minimum resolution score for a project to be considered a candidate at all.</summary>
    public double ResolutionMinScore { get; set; } = 0.15;

    /// <summary>
    /// Relative confidence (top / (top + runner-up)) the best candidate must reach to be
    /// picked without asking. Below it, with a viable runner-up, the resolver asks to clarify.
    /// </summary>
    public double ResolutionConfidenceThreshold { get; set; } = 0.65;

    /// <summary>How many relevant open loops to surface per turn.</summary>
    public int MaxOpenLoops { get; set; } = 3;

    /// <summary>Minimum number of related memories before they're consolidated (don't overgeneralize).</summary>
    public int ConsolidationMinObservations { get; set; } = 3;

    /// <summary>Similarity at/above which same-slot memories are considered the same topic for consolidation.</summary>
    public double ConsolidationMinSimilarity { get; set; } = 0.4;
}

/// <summary>Weights applied to each retrieval signal before summation.</summary>
public sealed class RetrievalWeights
{
    public double SemanticSimilarity { get; set; } = 1.0;
    public double KeywordOverlap { get; set; } = 0.6;
    public double Recency { get; set; } = 0.3;
    public double Importance { get; set; } = 0.3;
    public double Confidence { get; set; } = 0.2;
    public double ProjectAssociation { get; set; } = 0.5;
    public double OpenLoopBoost { get; set; } = 0.4;
}
