namespace Companion.Core.Domain;

/// <summary>
/// One system-level decision made during a turn, recorded so "why did she do that?" has an
/// answer that points at OUR architecture rather than at the chat model's opaque judgment.
/// These are decisions the pipeline already makes — the record adds no new authority, it makes
/// the existing authority inspectable. The list of stages will grow as decisions that today
/// live implicitly inside the generation prompt are promoted into system code
/// (see docs/LANGUAGE_ORGAN.md).
/// </summary>
public sealed record DecisionRecord
{
    /// <summary>Which decision this is: "privacy", "roleplay", "register", "project",
    /// "curiosity", "packet.budget", "reply.gate", "extraction", …</summary>
    public required string Stage { get; init; }

    /// <summary>What made the call: "rule" (deterministic code), "model" (an LLM or
    /// classifier verdict), or "config" (an operator switch).</summary>
    public required string Decider { get; init; }

    /// <summary>The decision itself, as a short stable string — comparable across turns.</summary>
    public required string Verdict { get; init; }

    /// <summary>The decider's own confidence, when it has one. Null for rules and switches —
    /// a rule is not 100% confident, it is not making a probabilistic claim at all.</summary>
    public double? Confidence { get; init; }

    /// <summary>Bounded human-readable context for the verdict (never secrets, never the
    /// full prompt).</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// One retrieved memory as it entered the context, structured for callers that need to check
/// retrieval did its job (the synthetic evaluator, the soak harness) rather than eyeball it.
/// The prose twin <see cref="TurnDiagnostics.RetrievedSummaries"/> stays for humans.
/// </summary>
public sealed record RetrievedMemoryTrace
{
    /// <summary>Bounded preview of the memory content (same bound as the prose summaries).</summary>
    public required string Content { get; init; }

    public required double Score { get; init; }

    /// <summary>Where it came from: "retrieval" (ranked) or "associative" (expansion).</summary>
    public string? Source { get; init; }
}
