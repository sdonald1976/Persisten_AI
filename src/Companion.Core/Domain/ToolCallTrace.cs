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
    public required DateTimeOffset At { get; init; }

    /// <summary>First characters of the user message (bounded), for orientation only.</summary>
    public string? UserMessagePreview { get; init; }

    public int MemoriesRetrieved { get; init; }

    /// <summary>Bounded summaries of the memories that entered context, with scores.</summary>
    public IReadOnlyList<string> RetrievedSummaries { get; init; } = Array.Empty<string>();

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
