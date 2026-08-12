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
