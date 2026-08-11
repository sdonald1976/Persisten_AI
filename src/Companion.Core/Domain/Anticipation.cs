namespace Companion.Core.Domain;

/// <summary>
/// A dated thing in the user's life the companion is looking forward to with them — "the
/// interview on Thursday", "your sister's graduation on Saturday". It exists so the companion can
/// care at the <em>right moments</em>: encouragement on the day ("good luck today"), a follow-up
/// once it's passed ("how did it go?"), each voiced at most once, then the anticipation closes.
/// One that never got its follow-up quietly expires — she doesn't ask about an interview from
/// three weeks ago.
/// </summary>
public sealed class Anticipation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>User-facing phrase, ready to drop into a sentence ("your interview", "the recital").</summary>
    public string Description { get; set; } = default!;

    /// <summary>The event day (local midnight). Day precision — that's how people talk about plans.</summary>
    public DateTimeOffset EventAt { get; set; }

    /// <summary>The user's words that created it (provenance, like everywhere else).</summary>
    public string? Evidence { get; set; }

    /// <summary>The message it was detected in.</summary>
    public Guid? SourceMessageId { get; set; }

    public AnticipationStatus Status { get; set; } = AnticipationStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EncouragedAt { get; set; }
    public DateTimeOffset? FollowedUpAt { get; set; }

    /// <summary>Still worth speaking about (not yet followed-up or expired).</summary>
    public bool IsOpen => Status is AnticipationStatus.Pending or AnticipationStatus.Encouraged;
}

public enum AnticipationStatus
{
    /// <summary>Held; nothing voiced yet.</summary>
    Pending,

    /// <summary>"Good luck" was said (on/near the day). The follow-up is still owed.</summary>
    Encouraged,

    /// <summary>"How did it go?" was asked — the arc is complete.</summary>
    FollowedUp,

    /// <summary>The moment passed unremarked for too long; let go rather than dredged up.</summary>
    Expired,
}
