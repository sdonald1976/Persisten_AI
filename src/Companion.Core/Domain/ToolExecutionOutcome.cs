namespace Companion.Core.Domain;

/// <summary>
/// The TYPED outcome of one tool call, captured at execution time — before anything is
/// converted into production prompt text (Source 2). Shadow consumers read this; nothing
/// parses <c>ResultsSection</c>, rendered JSON, or prose.
///
/// Multiple calls in one turn stay independently attributable through
/// <see cref="ToolCallId"/>.
/// </summary>
public sealed record ToolExecutionOutcome
{
    /// <summary>Stable per-call identity; survives retries of the same call.</summary>
    public required string ToolCallId { get; init; }

    public required string Tool { get; init; }
    public string ToolVersion { get; init; } = "1";

    /// <summary>The turn that requested it, and the intent that motivated it.</summary>
    public required Guid RequestingTraceId { get; init; }
    public string? RequestingIntent { get; init; }

    public required bool Requested { get; init; }

    /// <summary>False when the tool layer refused; <see cref="RefusalReason"/> says why.</summary>
    public required bool Authorized { get; init; }
    public string? RefusalReason { get; init; }

    public required bool Executed { get; init; }
    public required ToolExecutionStatus Status { get; init; }

    /// <summary>The structured payload — the tool's own typed data, never prose.</summary>
    public object? StructuredResult { get; init; }

    /// <summary>A short, content-safe failure summary: code and plain phrase only. Never a
    /// stack trace, exception text, or provider payload.</summary>
    public string? SafeFailureSummary { get; init; }

    /// <summary>The tool layer's disclosure decision, separate from expression.</summary>
    public required bool DisclosurePermitted { get; init; }

    /// <summary>Principals the result may reach; empty = the turn's participants.</summary>
    public IReadOnlyList<string> AuthorizedAudience { get; init; } = [];

    /// <summary>full | no_training | no_telemetry_text | volatile_turn_only.</summary>
    public string Retention { get; init; } = "no_training";

    public string Provenance { get; init; } = "tool";

    /// <summary>Set by SECRET DETECTION before any promotion is considered.</summary>
    public bool ContainsSecret { get; init; }

    /// <summary>
    /// The PLANNER's typed decision about this result. Cognition decides expression; the
    /// tool only supplies a value. Promotion here still cannot override authorization,
    /// disclosure, recipient, or retention.
    /// </summary>
    public required ToolPlannerDisposition PlannerDisposition { get; init; }

    public string? RelatedActivityInstanceId { get; init; }
    public Guid? RelatedProjectId { get; init; }

    public long DurationMs { get; init; }
}

public enum ToolExecutionStatus { NotExecuted, Succeeded, Failed, Cancelled, TimedOut }

/// <summary>What cognition wants done with a result — never what the tool wants.</summary>
public enum ToolPlannerDisposition { Withheld, BackgroundOnly, MayExpress, MustExpress }
