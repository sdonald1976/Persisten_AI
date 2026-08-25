namespace Companion.Core.Domain;

/// <summary>
/// One durable, versioned TRANSITION of the companion's spirits (Source 4b). Append-only:
/// her mood has a history rather than only a current value, and every reading of it can name
/// the exact event it came from.
///
/// This exists because a register vote needs provenance that resolves. A hash of the current
/// mutable spirits value is not provenance — it identifies nothing, survives nothing, and
/// changes the moment the value does. A transition row is a real thing to point at.
///
/// <see cref="Id"/> is the <c>StateRef</c> a vote cites. <see cref="Version"/> is monotonic
/// per user and unique, which is also what makes concurrent nudges safe.
/// </summary>
public sealed class CompanionMoodTransition
{
    /// <summary>The StateRef: stable identity of this transition, cited by register votes.</summary>
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    /// <summary>Monotonic per user, starting at 1. Unique with UserId — the concurrency guard.</summary>
    public int Version { get; set; }

    /// <summary>Effective spirits BEFORE this transition (already decayed to the moment).</summary>
    public double PreviousSpirits { get; set; }

    /// <summary>Spirits after applying the nudge. This is what the next read decays from.</summary>
    public double NewSpirits { get; set; }

    /// <summary>The moment's valence in [-1, 1] that caused the move. Metadata, not content:
    /// a number, never the user's words or the cue that produced it.</summary>
    public double AppliedValence { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
