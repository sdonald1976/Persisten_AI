namespace Companion.Core.Domain;

/// <summary>
/// Per-turn diagnostics. Memory bugs are otherwise very hard to diagnose, so every turn
/// records what was retrieved, the scores, why, what was excluded, and the exact context
/// sent to the model. Surfaced via the CLI `/why` command.
/// </summary>
public sealed record TurnTrace
{
    /// <summary>Correlates this trace with the diagnostics ring entry for the same turn.</summary>
    public Guid TraceId { get; init; } = Guid.NewGuid();

    public required string UserMessage { get; init; }

    /// <summary>
    /// The turn's control-flow outcome. When not <see cref="TurnStatus.Answered"/>, the normal
    /// generation + memory pipeline was deliberately skipped (see the ambiguity handling).
    /// </summary>
    public TurnStatus Status { get; init; } = TurnStatus.Answered;

    /// <summary>Set when a clarification was requested/resolved this turn — the pending record's id.</summary>
    public Guid? PendingClarificationId { get; init; }

    /// <summary>Project the turn was associated with, if any was detected.</summary>
    public string? DetectedProject { get; init; }

    /// <summary>Memories that were selected (in rank order).</summary>
    public required IReadOnlyList<RetrievalResult> Retrieved { get; init; }

    /// <summary>Memories that were scored but excluded (below cutoff / over budget), with reasons.</summary>
    public required IReadOnlyList<RetrievalResult> Excluded { get; init; }

    /// <summary>The context packet that was assembled for the model.</summary>
    public required ContextPacket Packet { get; init; }

    /// <summary>The assistant response produced.</summary>
    public required string Response { get; init; }

    /// <summary>Candidate memories extracted this turn and how the pipeline decided on each.</summary>
    public MemoryExtractionResult Extraction { get; init; } = MemoryExtractionResult.Empty;

    /// <summary>How the turn's project reference resolved, plus the project summary and open loops.</summary>
    public ProjectContext ProjectContext { get; init; } = ProjectContext.Empty;

    /// <summary>Project/open-loop state changes made after the turn (step 10).</summary>
    public ProjectUpdateResult ProjectUpdates { get; init; } = ProjectUpdateResult.Empty;

    /// <summary>Tool names the model was offered this turn.</summary>
    public IReadOnlyList<string> AdvertisedTools { get; init; } = Array.Empty<string>();

    /// <summary>Tools actually invoked this turn (validated args, outcome, duration).</summary>
    public IReadOnlyList<ToolCallTrace> ToolCalls { get; init; } = Array.Empty<ToolCallTrace>();
}
