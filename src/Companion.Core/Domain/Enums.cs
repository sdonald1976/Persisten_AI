namespace Companion.Core.Domain;

/// <summary>Which kind of memory a record is. Discriminates without inheritance.</summary>
public enum MemoryKind
{
    Semantic,
    Episodic,
}

/// <summary>
/// The lifecycle state shared by all memories.
/// Candidate -> Active -> (Superseded | Disputed | Deleted).
/// Only <see cref="Active"/> memories are surfaced as current.
/// </summary>
public enum MemoryStatus
{
    /// <summary>Proposed by extraction, not yet validated/trusted.</summary>
    Candidate,

    /// <summary>Accepted, current, retrievable.</summary>
    Active,

    /// <summary>Replaced by a newer fact; kept for history, not presented as current.</summary>
    Superseded,

    /// <summary>User flagged it wrong; demoted/annotated until resolved.</summary>
    Disputed,

    /// <summary>Soft-deleted; must never be retrieved, summarized, or embedded again.</summary>
    Deleted,
}

/// <summary>
/// The episodic record's own status ("what happened, and where it stands"), distinct
/// from the memory lifecycle. Planned/InProgress episodes act as open loops in Phase 2.
/// </summary>
public enum EpisodeStatus
{
    Occurred,
    Planned,
    InProgress,
    Resolved,
    Abandoned,
}

/// <summary>Temporal validity of a semantic fact.</summary>
public enum Validity
{
    /// <summary>Believed true now.</summary>
    Current,

    /// <summary>Was true in the past; not asserted now (e.g. a replaced device).</summary>
    Historical,

    /// <summary>Explicitly short-lived (e.g. "eating low carb this week").</summary>
    Temporary,

    /// <summary>Replaced by a newer contradicting fact.</summary>
    Superseded,
}

/// <summary>How precisely an event time is known.</summary>
public enum TimePrecision
{
    Exact,
    Day,
    Month,
    Year,
    Approximate,
}

/// <summary>Author of a message.</summary>
public enum MessageRole
{
    User,
    Assistant,
    System,
}
