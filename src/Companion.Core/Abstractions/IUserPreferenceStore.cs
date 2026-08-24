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
    /// /forget support: deactivates (EvidenceForgotten) every active record whose
    /// EvidenceMessageId is in <paramref name="messageIds"/> or whose EvidenceStatement
    /// mutually contains one of <paramref name="excerpts"/> (case-insensitive, either
    /// direction), and PURGES the statement so the forgotten text does not linger.
    /// Returns how many were invalidated.
    /// </summary>
    Task<int> InvalidateByForgottenEvidenceAsync(
        string userId, IReadOnlyCollection<string> excerpts, IReadOnlyCollection<Guid> messageIds,
        DateTimeOffset now, CancellationToken ct = default);
}
