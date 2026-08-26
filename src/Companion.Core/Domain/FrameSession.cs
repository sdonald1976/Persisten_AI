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

    /// <summary>Optimistic-concurrency token. A losing writer sees a conflict rather than
    /// silently overwriting a transition somebody else already applied.</summary>
    public int Version { get; set; }

    /// <summary>Idempotency keys already applied (normally turn trace ids), serialized.
    /// A retried turn returns the existing session instead of transitioning twice.</summary>
    public string AppliedKeysJson { get; set; } = "[]";

    /// <summary>Append-only transition log: kind, when, and the EVENT that caused it.
    /// This is what makes "she never entered" and "she stayed in after I said stop"
    /// separable after the fact.</summary>
    public string TransitionLogJson { get; set; } = "[]";
}

public enum FrameSessionStatus { Active, Ended }

/// <summary>
/// One recorded transition.
///
/// <para><see cref="EvidenceMessageId"/> is an EXACT DURABLE IDENTITY, never the user's
/// words. The log needs to answer "which turn moved the frame", and a message id answers
/// that precisely; an excerpt answers it approximately while also becoming a second
/// transcript that <c>/forget</c> could not reach. The transition kind, the timestamp and
/// the content-safe <c>Cause</c> token are the operational state, and they survive
/// forgetting — only the link to the message is severed.</para>
///
/// <para>Null means either that no message caused this transition, or that the causing
/// message was forgotten. Those are deliberately indistinguishable here: preserving the
/// difference would preserve the fact that something was forgotten, which is itself a
/// residue of the forgotten turn.</para>
/// </summary>
public sealed record FrameTransitionEntry(
    string Transition, DateTimeOffset At, string Cause, Guid? EvidenceMessageId);

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

    /// <summary>
    /// The STRUCTURED subject of the boundary — what it governs, in the vocabulary
    /// enforcement uses ("no third-person narration"), not the sentence the user typed.
    /// This is the value enforcement reads, so it is retained; it is a classification
    /// rather than a quotation.
    /// </summary>
    public string Subject { get; set; } = default!;

    public DateTimeOffset StatedAt { get; set; }
    public string EvidenceKind { get; set; } = "direct-instruction";

    /// <summary>
    /// Exact durable identity of the message that stated the boundary. This is the whole
    /// of the evidence: the wording itself is deliberately not stored, because a verbatim
    /// statement kept "as evidence" is a copy of the user's words living outside the
    /// transcript, and the transcript is the thing <c>/forget</c> is defined against.
    /// </summary>
    public Guid? EvidenceMessageId { get; set; }

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

    /// <summary>The evidence behind it was forgotten; the link to the message is severed
    /// and the boundary stops applying.</summary>
    EvidenceForgotten,
}
