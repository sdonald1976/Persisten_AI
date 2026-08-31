namespace Companion.Core.Domain;

/// <summary>
/// A conservative relevance label for one (turn, memory) pair. Tri-state on purpose: the
/// pipeline can prove a memory was *available* mechanically, but "was it actually relevant" is
/// often not mechanically decidable, and guessing wrong poisons a training set.
///
/// The hard rule (Scott's constraint): <see cref="Unknown"/> is NEVER trained as a negative. A
/// memory that went unexpressed because suppression, question policy, brevity, or another plan
/// constraint prevented it is Unknown, not Negative.
/// </summary>
public enum MemoryRelevanceLabel
{
    /// <summary>Strong mechanical evidence the memory was used (referenced by a plan item that
    /// authorized expression, and lexically surfaced in the reply). High precision, low recall.</summary>
    Positive,

    /// <summary>Strong mechanical evidence the memory was NOT relevant: it was available and
    /// nothing (suppression, policy, brevity, exclusion, a failed turn) prevented its use, and it
    /// still did not surface. Deliberately rare.</summary>
    Negative,

    /// <summary>Everything else. The default, and the only honest label for most pairs. Routed to
    /// the human-review queue; never used as a negative training example.</summary>
    Unknown,
}

/// <summary>Whether a memory's content surfaced in the displayed reply. Expression is a semantic
/// question; the mechanical signal here is a lexical proxy, so it is explicitly marked inferred.</summary>
public enum ExpressionEvidence
{
    /// <summary>Not attempted (e.g. the turn failed before a reply, or the memory was excluded).</summary>
    NotEvaluated,

    /// <summary>Lexical overlap suggests the content surfaced. INFERRED — a proxy, not proof.</summary>
    LikelyExpressed,

    /// <summary>No lexical overlap. The memory may still have influenced the reply implicitly, so
    /// this is not by itself a negative label.</summary>
    NotObservablyExpressed,
}

/// <summary>Why a retrieved memory did not reach the reply, when that is mechanically known.</summary>
public enum MemoryExclusionReason
{
    None,
    BelowRelevanceFloor,   // filtered by the retriever's score/floor
    NotSelected,           // ranked but outside TopK
    TrimmedFromPacket,     // dropped by the context-packet token budget
    SuppressedByPlan,      // a must_not_express / background_only / privacy item covers it
    TurnFailedOrAborted,   // no reply was produced
}

/// <summary>
/// One memory's journey through a single turn, id-preserving and turn-correlated. Additive and
/// versioned: it is recorded post-turn, reads only what the pipeline already produced, and can
/// never alter the displayed reply or any cognitive state.
///
/// Each boolean/enum stage is annotated in doc-comments as MECHANICAL (observed directly from a
/// typed artifact) or INFERRED (a proxy needing review). The <see cref="Label"/> is derived
/// conservatively from the mechanical stages only.
/// </summary>
public sealed record MemoryProvenance
{
    public const int SchemaVersion = 1;

    public required Guid TurnId { get; init; }
    public required Guid MemoryId { get; init; }

    // ---- MECHANICAL stages (from typed artifacts, id-preserving) ----

    /// <summary>MECHANICAL: in the retrieval candidate set.</summary>
    public required bool Retrieved { get; init; }

    /// <summary>MECHANICAL: the reranker's score for this memory, if reranking ran.</summary>
    public double? RerankerScore { get; init; }

    /// <summary>MECHANICAL: 0-based rank after reranking, if reranking ran.</summary>
    public int? RerankerRank { get; init; }

    /// <summary>MECHANICAL: survived retrieval selection AND the packet token budget.</summary>
    public required bool RetainedInPacket { get; init; }

    /// <summary>MECHANICAL: the id of a typed plan item that carries this memory, or null.</summary>
    public string? ReferencedByPlanItemId { get; init; }

    /// <summary>MECHANICAL: the policy of the referencing plan item (must_express, may_express,
    /// background_only, must_not_express, admit_unknown…), or null when unreferenced.</summary>
    public string? PlanItemPolicy { get; init; }

    /// <summary>MECHANICAL: available FOR EXPRESSION this turn — visible in the packet or on a plan item, AND not
    /// suppressed. A must_not_express memory is visible but not available for expression.</summary>
    public required bool AvailableToMouth { get; init; }

    /// <summary>MECHANICAL: the reason it did not reach the reply, when known.</summary>
    public MemoryExclusionReason ExclusionReason { get; init; } = MemoryExclusionReason.None;

    // ---- INFERRED stage ----

    /// <summary>INFERRED: lexical proxy for whether the content surfaced in the displayed reply.</summary>
    public ExpressionEvidence Expressed { get; init; } = ExpressionEvidence.NotEvaluated;

    // ---- derived, conservative ----

    /// <summary>Tri-state, derived from mechanical stages only. Unknown by default.</summary>
    public required MemoryRelevanceLabel Label { get; init; }

    /// <summary>One line naming the mechanical basis for the label — for audit and review.</summary>
    public required string LabelBasis { get; init; }
}
