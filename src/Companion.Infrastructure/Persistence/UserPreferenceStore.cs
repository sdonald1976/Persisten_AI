using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF-backed store for explicit user preferences (Source 3). Lifecycle is insert-and-link:
/// StateAsync supersedes the slot's active record in the same transaction, so exactly one
/// record per (kind, scope, dimension[, subject]) is ever active; RevokeAsync deactivates
/// with the revocation's own evidence and creates nothing.
/// </summary>
internal sealed class UserPreferenceStore(CompanionDbContext db) : IUserPreferenceStore
{
    public async Task<IReadOnlyList<UserPreferenceRecord>> GetActiveAsync(
        string userId, CancellationToken ct = default)
        => await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.Status == UserPreferenceStatus.Active)
            .OrderByDescending(p => p.StatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserPreferenceRecord>> GetAllAsync(
        string userId, CancellationToken ct = default)
        => await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.StatedAt)
            .ToListAsync(ct);

    public async Task<UserPreferenceRecord> StateAsync(
        UserPreferenceRecord record, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var slot = await ActiveSlotAsync(
            record.UserId, record.Kind, record.Scope, record.Dimension, record.Subject, ct);

        if (record.Id == Guid.Empty)
            record.Id = Guid.NewGuid();
        db.UserPreferences.Add(record);

        if (slot is not null)
        {
            slot.Status = UserPreferenceStatus.Superseded;
            slot.SupersededById = record.Id;
            slot.DeactivatedAt = record.StatedAt;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<UserPreferenceRecord?> RevokeAsync(
        string userId, UserPreferenceKind kind, string scope, string dimension,
        DateTimeOffset revokedAt, Guid? evidenceMessageId, string? revocationStatement,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var slot = await ActiveSlotAsync(userId, kind, scope, dimension, subject: null, ct);
        if (slot is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        slot.Status = UserPreferenceStatus.Revoked;
        slot.RevokedAt = revokedAt;
        slot.DeactivatedAt = revokedAt;
        slot.RevocationEvidenceMessageId = evidenceMessageId;
        slot.RevocationStatement = revocationStatement;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return slot;
    }

    public async Task<int> InvalidateByForgottenEvidenceAsync(
        string userId, IReadOnlyCollection<string> excerpts, IReadOnlyCollection<Guid> messageIds,
        DateTimeOffset now, CancellationToken ct = default)
    {
        // Meaningful excerpts only — the same bar the shadow sweep applies, so a two-word
        // fragment cannot take out an unrelated preference.
        var usable = excerpts
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Trim().Length >= 12)
            .Select(e => e.Trim())
            .ToList();

        var candidates = await db.UserPreferences
            .Where(p => p.UserId == userId && p.Status == UserPreferenceStatus.Active)
            .ToListAsync(ct);

        var invalidated = 0;
        foreach (var p in candidates)
        {
            var byMessage = p.EvidenceMessageId is { } mid && messageIds.Contains(mid);
            var byStatement = p.EvidenceStatement is { } stmt && usable.Any(e =>
                stmt.Contains(e, StringComparison.OrdinalIgnoreCase)
                || e.Contains(stmt, StringComparison.OrdinalIgnoreCase));
            if (!byMessage && !byStatement)
                continue;

            p.Status = UserPreferenceStatus.EvidenceForgotten;
            p.DeactivatedAt = now;
            // The authority is gone WITH its text: a forgotten statement must not linger
            // in this table as a searchable copy of what the user asked to forget.
            p.EvidenceStatement = null;
            invalidated++;
        }

        if (invalidated > 0)
            await db.SaveChangesAsync(ct);
        return invalidated;
    }

    private Task<UserPreferenceRecord?> ActiveSlotAsync(
        string userId, UserPreferenceKind kind, string scope, string dimension,
        string? subject, CancellationToken ct)
        => db.UserPreferences.FirstOrDefaultAsync(p =>
            p.UserId == userId
            && p.Status == UserPreferenceStatus.Active
            && p.Kind == kind
            && p.Scope == scope
            && p.Dimension == dimension
            // Restrictions are per-subject slots; register slots ignore subject.
            && (kind != UserPreferenceKind.ExpressionRestriction || p.Subject == subject), ct);
}
