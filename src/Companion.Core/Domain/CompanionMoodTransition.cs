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

    /// <summary>
    /// Effective spirits BEFORE this transition (already decayed to the moment). NULL on a
    /// BASELINE row: a baseline has no predecessor by construction, which is precisely what
    /// makes it opaque — there is no earlier value to reconstruct anything from.
    /// </summary>
    public double? PreviousSpirits { get; set; }

    /// <summary>Spirits after applying the nudge. This is what the next read decays from.</summary>
    public double NewSpirits { get; set; }

    /// <summary>
    /// The moment's valence in [-1, 1] that caused the move. NULL once the evidence behind it
    /// was forgotten — a valence is a reading OF something the user said, so it is purged
    /// here for the same reason it is purged from the signal itself.
    ///
    /// KNOWN RESIDUAL (see SOURCE4_RESULTS.md): purging this field removes the stored
    /// derivative but not the arithmetic. Her spirits trajectory is a deterministic function
    /// of the valences that moved it, so the neighbouring transitions bracket a redacted one
    /// exactly. Closing that needs a decision about whether forgetting a moment should also
    /// un-move her mood; it is reported, not silently assumed.
    /// </summary>
    public double? AppliedValence { get; set; }

    /// <summary>
    /// The evidence event this nudge came from, when it came from one — the link that lets
    /// /forget find the transitions a forgotten moment produced. Null for nudges with no
    /// emotional-signal origin.
    /// </summary>
    public Guid? SourceEvidenceEventId { get; set; }

    /// <summary>True once the evidence behind this transition was forgotten.</summary>
    public bool EvidenceForgotten { get; set; }

    /// <summary>
    /// A PRIVACY-COMPACTION BASELINE: the opaque starting point written when /forget removed a
    /// reconstructable chain. It carries her spirits as they stood at that moment and nothing
    /// about how they got there — no predecessor, no applied valence, no source event.
    ///
    /// Her present mood is deliberately NOT rewound: forgetting an evidence removes the record
    /// of it, not the fact that she was affected. What it does remove is every row from which
    /// the forgotten valence could be recomputed, so the arithmetic that survived plain
    /// redaction has nothing left to work on.
    ///
    /// Exact replay across a baseline is intentionally unavailable, and diagnosed as such
    /// rather than silently approximated.
    /// </summary>
    public bool IsBaseline { get; set; }

    /// <summary>When this baseline was written, for audit. Null on ordinary transitions.</summary>
    public DateTimeOffset? CompactedAt { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
