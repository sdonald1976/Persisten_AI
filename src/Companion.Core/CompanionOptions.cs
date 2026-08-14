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

    /// <summary>
    /// Soft ceiling for the WHOLE rendered packet. Exceeding it logs a warning and shows up in
    /// turn diagnostics rather than silently truncating: on a small local model a bloated prompt
    /// degrades replies long before it errors, and quietly dropping context would trade a visible
    /// problem for an invisible one. Set to 0 to disable the warning.
    /// </summary>
    public int PacketTokenWarningThreshold { get; set; } = 3000;

    /// <summary>
    /// Hard ceiling for the rendered prompt, in tokens. Sections are dropped lowest-value first
    /// to stay under it; identity and the standing rules are never among them. Zero disables the
    /// limit, which should only ever be a deliberate choice for a model with room to spare.
    ///
    /// This is not the same idea as <see cref="PacketTokenWarningThreshold"/>, and the difference
    /// is the whole point. That one warns; this one acts. A warning is the right response to a
    /// prompt that is merely getting fat, but a prompt that exceeds the model's context window is
    /// not degraded, it is mutilated: the server discards the overflow from the top — identity,
    /// standing rules, the oldest turns — and answers from the remainder without reporting
    /// anything. The reply comes back fluent, confident, and severed from everything the
    /// companion is supposed to be.
    ///
    /// Set from the chat endpoint's context window at startup, minus room for the reply. Left at
    /// this default it fits the smallest window in common use, because being needlessly frugal
    /// costs a little nuance and guessing high costs her identity.
    /// </summary>
    public int PromptTokenBudget { get; set; } = 3000;

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

    /// <summary>Master switch for the between-session reflection pass (the inner monologue).</summary>
    public bool EnableReflection { get; set; } = true;

    /// <summary>How long the user must have been quiet before a reflection pass may run.</summary>
    public int ReflectionIdleMinutes { get; set; } = 30;

    /// <summary>How often the background worker checks whether it's time to reflect.</summary>
    public int ReflectionCheckMinutes { get; set; } = 5;

    /// <summary>
    /// Minimum new user messages since the last watermark before a pass runs — a one-line visit
    /// isn't worth a thought, and the guard keeps quiet days from costing model calls.
    /// </summary>
    public int ReflectionMinNewMessages { get; set; } = 3;

    /// <summary>Cap on messages one pass reads (a long backlog keeps the freshest).</summary>
    public int ReflectionMaxMessages { get; set; } = 60;

    /// <summary>Max curiosities kept from one pass — wonder about a couple of things, not everything.</summary>
    public int ReflectionMaxCuriosities { get; set; } = 2;

    /// <summary>
    /// Minimum time between voicing two curiosities. One question, then let the conversation
    /// breathe — this is what keeps proactive curiosity from feeling like an interview.
    /// </summary>
    public double CuriosityCooldownHours { get; set; } = 1.0;

    /// <summary>
    /// Let the chat model intentionally invoke read-only tools (memory search, capability list,
    /// diagnostics, …) before replying. Adds up to MaxToolCallsPerTurn+1 model calls to a turn
    /// when the model chooses to look things up; offline mocks never produce a tool call.
    /// </summary>
    public bool EnableToolUse { get; set; } = true;

    /// <summary>Hard ceiling on tool executions in one turn (identical repeats stop earlier).</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;

    /// <summary>
    /// Layer the LLM intent classifier on top of the deterministic rules (requires a real model).
    /// The rules always run first and always win when they match; the model may only promote a
    /// plain chat message to a read-only intent for phrasings the rules don't know.
    /// </summary>
    public bool UseLlmIntentParser { get; set; }

    public int MaxAttentionItems { get; set; } = 5;
    public int AttentionTtlDays { get; set; } = 7;
    public int MaxAssociativeMemories { get; set; } = 2;
    public double AssociativeMinStrength { get; set; } = 0.65;
    public int MaxProceduresInContext { get; set; } = 2;
    public int MaxSharedPerspectivesInContext { get; set; } = 3;
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
