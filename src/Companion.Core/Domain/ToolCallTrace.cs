namespace Companion.Core.Domain;

/// <summary>
/// The audited record of one tool invocation inside a turn: which tool, with what VALIDATED
/// arguments, how long it took, and a bounded summary of what came back. Tool calls live here and
/// in diagnostics — never in conversation history, and never as durable memory. The conversation
/// stays truthful; the trace stays inspectable.
/// </summary>
public sealed record ToolCallTrace
{
    public required string Tool { get; init; }

    /// <summary>The arguments as validated/normalized by the tool (never raw model text).</summary>
    public string? Arguments { get; init; }

    public required bool Ok { get; init; }

    /// <summary>"ok" or the failure code (invalid_arguments, not_found, unavailable, …).</summary>
    public required string Code { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Bounded JSON of the result handed to the model (what it actually saw).</summary>
    public string? ResultSummary { get; init; }
}

/// <summary>
/// One turn's operational story, kept in the in-memory diagnostics ring: what was retrieved,
/// which context sections were present, how generation went, and what tools ran. Powers
/// diagnostics.last_turn. Contains no secrets by construction — only names, counts, and bounded
/// content previews that were already going to the model anyway.
/// </summary>
public sealed record TurnDiagnostics
{
    /// <summary>Correlates this record with the in-process <see cref="TurnTrace"/> and any
    /// evaluation artifact derived from the turn. Unique per turn.</summary>
    public Guid TraceId { get; init; } = Guid.NewGuid();

    public required DateTimeOffset At { get; init; }

    /// <summary>First characters of the user message (bounded), for orientation only.</summary>
    public string? UserMessagePreview { get; init; }

    public int MemoriesRetrieved { get; init; }

    /// <summary>Bounded summaries of the memories that entered context, with scores.</summary>
    public IReadOnlyList<string> RetrievedSummaries { get; init; } = Array.Empty<string>();

    /// <summary>The same memories, structured — for harnesses that assert on retrieval
    /// rather than read it.</summary>
    public IReadOnlyList<RetrievedMemoryTrace> Retrieved { get; init; } = Array.Empty<RetrievedMemoryTrace>();

    /// <summary>System-level decisions the turn made, in pipeline order. Every entry was
    /// decided by OUR code or a role model we invoked — never inferred from the reply.</summary>
    public IReadOnlyList<DecisionRecord> Decisions { get; init; } = Array.Empty<DecisionRecord>();

    /// <summary>The turn's working-context read: open questions, topic, salient entities,
    /// reference resolution, move, and the raw vs resolved retrieval query. Ephemeral state,
    /// traced here and stored nowhere else.</summary>
    public WorkingContextState? WorkingContext { get; init; }

    /// <summary>When the retrieval query was rewritten AND capture is on: what the RAW message
    /// would have retrieved instead, same bounded format as <see cref="RetrievedSummaries"/>.
    /// The before/after evidence that resolution changes what reaches the prompt.</summary>
    public IReadOnlyList<string> RetrievedWithRawQuery { get; init; } = Array.Empty<string>();

    /// <summary>The turn's intent classification — SHADOW state (language-organ Phase 2):
    /// recorded here and captured for review, never given to generation until the shadow
    /// data earns it authority.</summary>
    public TurnIntentState? Intent { get; init; }

    /// <summary>SHADOW relevance feature under validation: do the retrieved memories contain
    /// the message's focal terms at all. Observed, consumed by nothing.</summary>
    public FocalCoverage? Focal { get; init; }

    /// <summary>The turn's ResponsePlan — SHADOW (Phase 5): what Ava decided, recorded
    /// beside what the model then said. The future renderer's input contract.</summary>
    public ResponsePlan? Plan { get; init; }

    /// <summary>Which packet sections were present this turn (mood, musing, temporal, …).</summary>
    public IReadOnlyList<string> ContextSections { get; init; } = Array.Empty<string>();

    public string? DetectedProject { get; init; }
    public bool InCharacterTurn { get; init; }
    public bool PrivateConversation { get; init; }

    public string? FinishReason { get; init; }
    public int GenerationRounds { get; init; }
    public string? ModelUsed { get; init; }

    public IReadOnlyList<string> AdvertisedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ToolCallTrace> ToolCalls { get; init; } = Array.Empty<ToolCallTrace>();

    /// <summary>The planner's verbatim (clipped) decisions, one per planning round — the answer
    /// to "did it decline, or produce something unusable?" when no tools ran.</summary>
    public IReadOnlyList<string> ToolDecisions { get; init; } = Array.Empty<string>();

    /// <summary>How many planner passes actually ran this turn (0 = nudge-only or disabled).</summary>
    public int PlanningRounds { get; init; }

    /// <summary>Estimated size of the rendered context packet the reply model received.</summary>
    public int PacketTokens { get; init; }
}
