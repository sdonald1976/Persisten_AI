namespace Companion.Core.Domain;

/// <summary>Which kind of memory a record is. Discriminates without inheritance.</summary>
public enum MemoryKind
{
    Semantic,
    Episodic,
}

/// <summary>
/// Whose story a memory belongs to. User memories are biography ("Scott likes ribeye");
/// Companion memories are about the companion herself; Shared memories are moments both were
/// part of ("the evening Scott tried teaching her poker") — not merely user facts, but history
/// they have together. Ownership shapes how a memory is presented, never whether it's true.
/// </summary>
public enum MemoryOwner
{
    User,
    Companion,
    Shared,
}

/// <summary>The outcome of a turn — whether it answered or paused for a control-flow reason.</summary>
public enum TurnStatus
{
    /// <summary>A normal answered turn (retrieval + generation + memory pipeline ran).</summary>
    Answered,

    /// <summary>The reference was ambiguous; the turn asked a clarifying question and ran nothing else.</summary>
    ClarificationRequested,

    /// <summary>This turn answered a pending clarification and resumed the original request.</summary>
    ClarificationResolved,

    /// <summary>The user cancelled a pending clarification ("never mind").</summary>
    ClarificationCancelled,
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

/// <summary>What the user's utterance is asking the companion to do (so slash commands aren't needed).</summary>
public enum IntentKind
{
    /// <summary>An ordinary conversational turn.</summary>
    Chat,

    /// <summary>"What do you remember about me / about X?"</summary>
    Recall,

    /// <summary>"Forget that / forget the X."</summary>
    Forget,

    /// <summary>"That's wrong / that's not right." (flag a memory as disputed)</summary>
    Dispute,

    /// <summary>"List my projects / what am I working on?"</summary>
    ListProjects,

    /// <summary>"What are my open loops / what's unfinished?"</summary>
    ListOpenLoops,

    /// <summary>"Consolidate your memories."</summary>
    Consolidate,

    /// <summary>"Set your persona to …" (replace the free-text style/persona).</summary>
    SetPersona,

    /// <summary>"Switch to the witty personality" / "what personalities are there?" (choose a preset).</summary>
    SetPersonality,

    /// <summary>"Your name is Ava" / "you're a woman" / "use she/her" (set the companion's identity).</summary>
    SetIdentity,

    /// <summary>"Be more concise / talk like …" (append a style directive).</summary>
    AdjustStyle,

    /// <summary>"That was great / perfect / helpful."</summary>
    FeedbackPositive,

    /// <summary>"That was unhelpful / a bad answer."</summary>
    FeedbackNegative,

    /// <summary>"Don't remember this conversation / private session." (skip durable memory)</summary>
    PrivacyDoNotRemember,

    /// <summary>A bare greeting ("hi", "hello") — answered with memory-grounded openers.</summary>
    Greeting,

    /// <summary>"What's on your mind / what are you thinking about?" — answered from the reflection diary.</summary>
    ShareThoughts,
}

/// <summary>How the user rated a reply — the signal a style fine-tune (DPO) later learns from.</summary>
public enum FeedbackRating
{
    Positive,
    Negative,
}

/// <summary>
/// Coarse emotional valence of a user's message, from a deterministic mood reading. Ordered from
/// most distressed to most upbeat so buckets can be compared and averaged as an index.
/// </summary>
public enum Sentiment
{
    VeryNegative,
    Negative,
    Neutral,
    Positive,
    VeryPositive,
}

/// <summary>Which way the user's mood has been moving across recent interactions.</summary>
public enum MoodTrend
{
    /// <summary>No clear direction — roughly level.</summary>
    Steady,

    /// <summary>More recent interactions are more positive than the earlier ones.</summary>
    Improving,

    /// <summary>More recent interactions are more negative than the earlier ones.</summary>
    Declining,
}

/// <summary>Where a semantic memory came from — so system-generated knowledge isn't presented as a direct quote.</summary>
public enum MemoryOrigin
{
    /// <summary>Stated by the user (a direct statement).</summary>
    Stated,

    /// <summary>Inferred by the system from one or more interactions.</summary>
    Inferred,

    /// <summary>A higher-level fact consolidated from several supporting memories.</summary>
    Consolidated,
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

/// <summary>What happened to a memory in an audit-trail entry.</summary>
public enum RevisionKind
{
    /// <summary>A new memory was accepted and created.</summary>
    Created,

    /// <summary>An existing fact/event was re-observed, refreshing its recency/confidence.</summary>
    Confirmed,

    /// <summary>An existing memory's fields were updated in place.</summary>
    Updated,

    /// <summary>A candidate was merged into an existing memory.</summary>
    Merged,

    /// <summary>Replaced by a newer contradicting fact (Phase 5).</summary>
    Superseded,

    /// <summary>Flagged as wrong by the user (Phase 5).</summary>
    Disputed,

    /// <summary>Soft-deleted (Phase 5).</summary>
    Deleted,
}

/// <summary>Lifecycle of a first-class project.</summary>
public enum ProjectStatus
{
    Active,
    Paused,
    Completed,
    Abandoned,
}

/// <summary>State of an unfinished matter (open loop / commitment).</summary>
public enum OpenLoopStatus
{
    /// <summary>Still unresolved.</summary>
    Open,

    /// <summary>Closed because it was done/answered.</summary>
    Resolved,

    /// <summary>Abandoned; will not be pursued.</summary>
    Cancelled,

    /// <summary>Intentionally deferred to later.</summary>
    Postponed,
}

/// <summary>What kind of entry a project activity-log event is.</summary>
public enum ProjectEventKind
{
    Created,
    Milestone,
    Note,
    StatusChanged,
    DecisionMade,
    Blocked,
    OpenLoopOpened,
    OpenLoopResolved,
}

/// <summary>The outcome of validating a single memory candidate through the pipeline.</summary>
public enum MemoryDecisionKind
{
    /// <summary>Accepted as a new memory.</summary>
    Accepted,

    /// <summary>Merged into / confirmed an existing memory rather than duplicating it.</summary>
    Merged,

    /// <summary>Discarded (e.g. missing evidence, too low confidence, duplicate noise).</summary>
    Rejected,

    /// <summary>Stored as a Candidate for later review (e.g. contradicts an existing fact).</summary>
    NeedsReview,

    /// <summary>Accepted as the current fact, superseding a contradicting existing one.</summary>
    Superseded,
}

public enum AttentionStatus { Active, Resolved, Expired, Dismissed }
public enum AttentionSourceType { Conversation, OpenLoop, Curiosity, Project, SharedExperience, Reflection, Correction, Capability }
public enum MemoryAssociationType { SameEvent, SameProject, SamePerson, FollowUp, Correction, CauseEffect, TopicRelated, SharedExperience }
public enum ProcedureOwner { UserTaught, CompanionLearned, SharedWorkflow }
public enum ProcedureStatus { Candidate, Active, Superseded, Archived }
public enum ProcedureAccess { Explainable, Executable }
public enum CapabilityAvailability { Available, Unavailable, Degraded, Untested }
public enum RetrievalSource { Direct, Associative }
