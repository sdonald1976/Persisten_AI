namespace Companion.Core.Domain;

/// <summary>
/// Per-turn diagnostics. Memory bugs are otherwise very hard to diagnose, so every turn
/// records what was retrieved, the scores, why, what was excluded, and the exact context
/// sent to the model. Surfaced via the CLI `/why` command.
/// </summary>
public sealed record TurnTrace
{
    public required string UserMessage { get; init; }

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
}
