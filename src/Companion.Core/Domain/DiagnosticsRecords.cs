namespace Companion.Core.Domain;

/// <summary>
/// One model invocation, persisted: which role asked (conversation, extraction, reranker, …),
/// which model answered, how long it took, what it cost in tokens, and whether it worked.
/// This is the raw material for judging models — latency and failure rates per role over real
/// usage, not vibes. Contains no prompt or reply text: sizes and outcomes only, so the log can
/// be kept for weeks without becoming a second (unguarded) conversation store.
/// </summary>
public class ModelCallRecord
{
    public Guid Id { get; set; }

    /// <summary>The job that made the call: conversation, extraction, summarizer, reranker, safety, task-auditor, embeddings.</summary>
    public string Role { get; set; } = default!;

    /// <summary>The operation: complete, stream, or embed.</summary>
    public string Operation { get; set; } = default!;

    /// <summary>The model that served it (as reported by the server, or the configured name).</summary>
    public string? Model { get; set; }

    public bool Ok { get; set; }

    /// <summary>The exception type name on failure (no message text — it may quote the prompt).</summary>
    public string? Error { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Input/output sizes in characters — always available, even when the server reports no usage.</summary>
    public int PromptChars { get; set; }
    public int CompletionChars { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// One tool invocation, persisted (the in-memory diagnostics ring forgets on restart; this
/// doesn't). Validated arguments only, bounded — never raw model output, never results.
/// </summary>
public class ToolCallRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    public string Tool { get; set; } = default!;

    /// <summary>The validated arguments JSON (bounded); null for calls refused before validation.</summary>
    public string? Arguments { get; set; }

    public bool Ok { get; set; }

    /// <summary>"ok" or the failure code (invalid_arguments, not_found, unavailable, timeout, …).</summary>
    public string Code { get; set; } = default!;

    public long DurationMs { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// One conversational turn's decision evidence, persisted (the in-memory ring keeps five and
/// forgets on restart — which is how the Epcot specimen's packet trace was lost before anyone
/// could inspect it; see docs/SPECIMENS.md). Enough to reconstruct WHY a turn behaved as it
/// did: working-context reading, intent, retrieval with both scores, decisions.
///
/// Privacy boundary: bounded previews only, and on a turn that is private, sensitive, or
/// in-character, every content field is null — structure (labels, counts, verdicts) survives,
/// words do not. Previews of ordinary turns mirror text the Messages table already stores
/// durably, so this adds inspectability, not a new exposure class. Pruned with the rest of
/// the diagnostics — this is telemetry, never autobiographical storage.
/// </summary>
public class TurnRecord
{
    /// <summary>The turn's TraceId — the same id the ring and TurnTrace carry.</summary>
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The user message this turn answered. The diagnostic previews below are
    /// derived from it, so forgetting it must reach them.</summary>
    public Guid? SourceMessageId { get; set; }

    /// <summary>Bounded user-message preview; null on private/sensitive/in-character turns.</summary>
    public string? UserPreview { get; set; }

    /// <summary>Bounded reply preview; null under the same conditions.</summary>
    public string? AssistantPreview { get; set; }

    /// <summary>Working-context move label ("answers-open-question", …).</summary>
    public string? Move { get; set; }

    public string? ResolvedReference { get; set; }
    public string? ResolutionConfidence { get; set; }
    public string? BoundQuestion { get; set; }

    /// <summary>What retrieval actually searched for after resolution; null on private turns.</summary>
    public string? RetrievalQuery { get; set; }

    /// <summary>Selected intent label and its confidence, plus the strongest competitor.</summary>
    public string? Intent { get; set; }
    public double IntentConfidence { get; set; }
    public string? IntentRunnerUp { get; set; }

    /// <summary>Compact JSON of retrieved items: [{"c":content,"s":score,"t":topical}], bounded.</summary>
    public string? Retrieved { get; set; }

    /// <summary>Focal terms (comma-joined) and coverage; null focal terms on private turns.</summary>
    public string? FocalTerms { get; set; }
    public bool? FocalCovered { get; set; }

    /// <summary>The decision trail, flattened: "stage=verdict; stage=verdict; …".</summary>
    public string Decisions { get; set; } = "";

    /// <summary>The turn's serialized ResponsePlan (Phase 5, shadow) — the future renderer
    /// input contract, preserved per turn; null on private/sensitive turns.</summary>
    public string? Plan { get; set; }

    public int PacketTokens { get; set; }
    public string? ModelUsed { get; set; }

    /// <summary>
    /// Additive (v1): the memory-provenance trace for this turn, as JSON — one record per
    /// (turn, memory) with id-preserving stages and a conservative tri-state relevance label.
    /// Null on private/sensitive turns and on turns with no retrieved memories. Existing
    /// consumers ignore it; the column is nullable so no historical row needs backfilling.
    /// </summary>
    public string? MemoryProvenance { get; set; }
}

/// <summary>Aggregated model telemetry for one role+model pair over a window (computed, not stored).</summary>
public sealed record ModelRoleStats
{
    public required string Role { get; init; }
    public required string? Model { get; init; }
    public required int Calls { get; init; }
    public required int Failures { get; init; }
    public required double AvgDurationMs { get; init; }
    public required long PromptTokens { get; init; }
    public required long CompletionTokens { get; init; }
    public required DateTimeOffset LastCalledAt { get; init; }
}
