namespace Companion.Core.Domain;

/// <summary>
/// One explicit, user-owned standing preference (Source 3) — a thing the USER said about
/// how Ava should speak or what she should not raise. Strictly distinct from
/// <see cref="CompanionPreference"/> (Ava's own tastes, which can never write here) and
/// from <see cref="UserProfile.Persona"/> (the descriptive legacy blob, which is neither
/// parsed nor migrated into these records).
///
/// Only an explicit instruction creates a record. Nothing is ever inferred from
/// annoyance, sentiment, subject matter, profanity use, repetition, or Ava's tastes —
/// there is no code path from any of those to this table.
///
/// Lifecycle is by INSERT-and-LINK, never mutation-in-place: an update inserts the new
/// record and marks the old one <see cref="UserPreferenceStatus.Superseded"/> with
/// <see cref="SupersededById"/>; a revocation marks it
/// <see cref="UserPreferenceStatus.Revoked"/> with its own evidence. History survives;
/// exactly the active rows have authority.
/// </summary>
public sealed class UserPreferenceRecord
{
    /// <summary>Stable identity — the value <c>provenance.evidenceRef</c> cites.</summary>
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public UserPreferenceKind Kind { get; set; }

    /// <summary>
    /// For <see cref="UserPreferenceKind.Register"/>: a plan/3 register dimension
    /// ("warmth", "verbosity", "profanity", …). For
    /// <see cref="UserPreferenceKind.ExpressionRestriction"/>: always "expression".
    /// </summary>
    public string Dimension { get; set; } = default!;

    /// <summary>
    /// Register kind: a closed-set token from the plan/3 schema for the dimension
    /// (e.g. "forbidden", "short", "mirror-only"). Restriction kind: "withhold".
    /// Never free text.
    /// </summary>
    public string Value { get; set; } = default!;

    /// <summary>ExpressionRestriction only: what must not be raised. User-stated.</summary>
    public string? Subject { get; set; }

    /// <summary>Whether this forbids rather than shapes.</summary>
    public bool Restrictive { get; set; }

    /// <summary>Only "global" exists. Other scopes are added when a requirement needs
    /// them, never invented from wording.</summary>
    public string Scope { get; set; } = "global";

    public UserPreferenceStatus Status { get; set; } = UserPreferenceStatus.Active;

    /// <summary>The record that replaced this one, when <see cref="Status"/> is Superseded.</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>
    /// The one-active invariant, enforced by the DATABASE rather than by transaction
    /// intent: "kind|scope|dimension[|subject]" while this record is Active, and NULL the
    /// moment it is not. A unique index on (UserId, ActiveSlot) makes a second active
    /// record for the same slot impossible — and because SQL treats NULLs as distinct,
    /// any number of deactivated rows coexist. Concurrent writers race to insert; the
    /// loser gets a constraint violation instead of a silently duplicated preference.
    /// </summary>
    public string? ActiveSlot { get; set; }

    /// <summary>Builds the slot key for a record's identity.</summary>
    public static string SlotKey(UserPreferenceKind kind, string scope, string dimension, string? subject)
        => kind == UserPreferenceKind.ExpressionRestriction
            ? $"{kind}|{scope}|{dimension}|{subject}"
            : $"{kind}|{scope}|{dimension}";

    /// <summary>When the user stated it — the effective time; newest wins among actives.</summary>
    public DateTimeOffset StatedAt { get; set; }

    /// <summary>When it stopped being active (superseded / revoked / evidence forgotten).</summary>
    public DateTimeOffset? DeactivatedAt { get; set; }

    // ---- evidence: resolvable, never copied into diagnostics ----

    /// <summary>
    /// The durable evidence EVENT this preference's authority rests on, minted at capture
    /// time — even when the intent path creates no Message row. Invalidation is by exact
    /// identity (this id, or <see cref="EvidenceMessageId"/>), never by text overlap: no
    /// preference may lose authority because unrelated forgotten text happens to resemble
    /// its statement.
    /// </summary>
    public Guid EvidenceEventId { get; set; }

    /// <summary>"direct-instruction" (the statement lives on this record, because the
    /// intent path stores no Message row) or "stored-message" (see
    /// <see cref="EvidenceMessageId"/>).</summary>
    public string EvidenceKind { get; set; } = "direct-instruction";

    public Guid? EvidenceMessageId { get; set; }

    /// <summary>The verbatim explicit command, when this record is its system of record.
    /// PURGED (set null) when the evidence it depends on is forgotten. Never appears in
    /// telemetry, register decisions, or shadow rows — those carry <see cref="Id"/> only.</summary>
    public string? EvidenceStatement { get; set; }

    // ---- revocation carries its own evidence ----

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevocationEvidenceMessageId { get; set; }
    public string? RevocationStatement { get; set; }
}

/// <summary>Register preferences and expression restrictions are different things
/// (a register setting shapes speech; a restriction forbids a subject) and travel
/// different mechanisms — votes vs must_not_express notes. One enum, two worlds.</summary>
public enum UserPreferenceKind { Register, ExpressionRestriction }

/// <summary>
/// The outcome of an evidence-driven invalidation sweep — counts only, content-safe.
/// `Ambiguous` counts forgotten statements that matched MORE than one active record by
/// exact equality: ambiguity must not revoke anything silently, so those records keep
/// their authority and the ambiguity is reported instead.
/// </summary>
public readonly record struct PreferenceInvalidationResult(int Invalidated, int Ambiguous);

public enum UserPreferenceStatus
{
    Active,
    Superseded,
    Revoked,

    /// <summary>The evidence this preference's authority depended on was forgotten via
    /// /forget. Deactivated immediately; the statement text is purged.</summary>
    EvidenceForgotten,
}
