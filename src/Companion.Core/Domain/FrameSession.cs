namespace Companion.Core.Domain;

/// <summary>
/// The authoritative owner of frame truth for one conversation.
///
/// `InCharacterDetector` may SUGGEST or ROUTE — a regex over asterisk markup is a hint about
/// what a turn looks like. It cannot own this, because the frame is a fact about the
/// conversation with a lifecycle, and "never entered character" and "stayed in character
/// after being asked to stop" are different failures that a detector reports identically.
///
/// This is operational frame METADATA, not fictional content: scene identity, transitions,
/// timestamps and the character roster. No scene content is ever stored here, which is what
/// keeps it retainable while the fiction itself is not.
/// </summary>
public sealed class FrameSession
{
    public Guid SessionId { get; set; }

    public string UserId { get; set; } = default!;
    public Guid ConversationId { get; set; }

    /// <summary>Scene identity. An identity, not a store — it says "the same scene as
    /// before" and cannot retrieve what happened in it.</summary>
    public string SceneRef { get; set; } = default!;

    public FrameSessionStatus Status { get; set; } = FrameSessionStatus.Active;

    /// <summary>The roster, serialized. Frame-local identities; never principals.</summary>
    public string CharactersJson { get; set; } = "[]";

    public string? ActiveCompanionCharacterId { get; set; }

    public string Narration { get; set; } = "forbidden";
    public string Continuity { get; set; } = "none";
    public string? NarratorKind { get; set; }
    public string? NarratorCharacterId { get; set; }
    public string? ViewpointCharacterId { get; set; }
    public string Person { get; set; } = "third";

    public DateTimeOffset EnteredAt { get; set; }
    public DateTimeOffset LastTransitionAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Append-only transition log: kind, when, and the evidence that caused it.
    /// This is what makes "she never entered" and "she stayed in after I said stop"
    /// separable after the fact.</summary>
    public string TransitionLogJson { get; set; } = "[]";
}

public enum FrameSessionStatus { Active, Ended }

/// <summary>One recorded transition. Evidence is the user's own words, bounded.</summary>
public sealed record FrameTransitionEntry(
    string Transition, DateTimeOffset At, string Cause, string? Evidence);

/// <summary>
/// A user boundary stated inside a frame — scene-scoped, never global.
///
/// Backing one with a global preference record would turn "no third-person narration in this
/// scene" into a standing instruction, which is exactly the over-reach the preference layer
/// was built to prevent.
/// </summary>
public sealed class FrameBoundaryRecord
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;
    public Guid ConversationId { get; set; }

    /// <summary>The exact frame this applies inside.</summary>
    public string SceneRef { get; set; } = default!;

    /// <summary>What the user asked for, as stated.</summary>
    public string Subject { get; set; } = default!;

    public DateTimeOffset StatedAt { get; set; }
    public string EvidenceKind { get; set; } = "direct-instruction";
    public Guid? EvidenceMessageId { get; set; }

    /// <summary>The verbatim statement. Purged when its evidence is forgotten.</summary>
    public string? EvidenceStatement { get; set; }

    public FrameBoundaryStatus Status { get; set; } = FrameBoundaryStatus.Active;
    public DateTimeOffset? DeactivatedAt { get; set; }
}

public enum FrameBoundaryStatus
{
    Active,

    /// <summary>The frame ended. It stops applying and is NOT deleted — the audit evidence
    /// survives, which is what keeps "she ignored my boundary" answerable afterwards.</summary>
    FrameEnded,

    Revoked,

    /// <summary>The evidence behind it was forgotten; the statement is purged.</summary>
    EvidenceForgotten,
}
