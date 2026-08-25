using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists the user's explicit standing preferences (Source 3). User-scoped like every
/// store. Writes go through the typed operations so lifecycle rules (supersession and
/// revocation are inserts-and-links, never in-place edits) cannot be bypassed.
/// </summary>
public interface IUserPreferenceStore
{
    /// <summary>Active records only — the set that currently has authority.</summary>
    Task<IReadOnlyList<UserPreferenceRecord>> GetActiveAsync(string userId, CancellationToken ct = default);

    /// <summary>Everything, for diagnostics and tests. Newest first.</summary>
    Task<IReadOnlyList<UserPreferenceRecord>> GetAllAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Records a new explicit preference. Any currently active record for the same
    /// (kind, scope, dimension) is marked Superseded with a link to the new one, in the
    /// same transaction — so exactly one record per slot is ever active.
    /// </summary>
    Task<UserPreferenceRecord> StateAsync(UserPreferenceRecord record, CancellationToken ct = default);

    /// <summary>
    /// Explicit revocation: deactivates the active record for (kind, scope, dimension)
    /// with the revocation's own evidence. Creates nothing — "you can swear again" is
    /// not a preference for swearing, it is the end of a restriction. Returns the
    /// revoked record, or null when nothing was active.
    /// </summary>
    Task<UserPreferenceRecord?> RevokeAsync(
        string userId, UserPreferenceKind kind, string scope, string dimension,
        DateTimeOffset revokedAt, Guid? evidenceMessageId, string? revocationStatement,
        CancellationToken ct = default);

    /// <summary>
    /// /forget support, EXACT-IDENTITY ONLY. Deactivates (EvidenceForgotten) and purges
    /// the statement of every active record whose EvidenceMessageId is in
    /// <paramref name="evidenceMessageIds"/> — exact id linkage. For the text-only
    /// /forget flow, <paramref name="forgottenStatements"/> are matched by NORMALIZED
    /// EXACT EQUALITY (trimmed, ordinal-ignore-case) against EvidenceStatement — never
    /// containment — and a statement that matches more than one active record is
    /// AMBIGUOUS: nothing is revoked for it, and the count is reported. Unrelated
    /// overlapping text can therefore never take a preference's authority.
    /// </summary>
    Task<PreferenceInvalidationResult> InvalidateByForgottenEvidenceAsync(
        string userId, IReadOnlyCollection<string> forgottenStatements,
        IReadOnlyCollection<Guid> evidenceMessageIds,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Exact invalidation by the durable evidence event minted at capture.</summary>
    Task<int> InvalidateByEvidenceEventAsync(
        string userId, Guid evidenceEventId, DateTimeOffset now, CancellationToken ct = default);
}
