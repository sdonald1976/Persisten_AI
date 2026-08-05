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

    /// <summary>Per-signal weights for the hybrid retrieval score.</summary>
    public RetrievalWeights Weights { get; set; } = new();
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
