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
        if (record.EvidenceEventId == Guid.Empty)
            record.EvidenceEventId = Guid.NewGuid();
        record.ActiveSlot = UserPreferenceRecord.SlotKey(
            record.Kind, record.Scope, record.Dimension, record.Subject);
        db.UserPreferences.Add(record);

        if (slot is not null)
        {
            slot.Status = UserPreferenceStatus.Superseded;
            slot.SupersededById = record.Id;
            slot.DeactivatedAt = record.StatedAt;
            // Vacating the slot is what lets the new row claim it under the unique index.
            slot.ActiveSlot = null;
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
        slot.ActiveSlot = null;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return slot;
    }

    public async Task<PreferenceInvalidationResult> InvalidateByForgottenEvidenceAsync(
        string userId, IReadOnlyCollection<string> forgottenStatements,
        IReadOnlyCollection<Guid> evidenceMessageIds,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var active = await db.UserPreferences
            .Where(p => p.UserId == userId && p.Status == UserPreferenceStatus.Active)
            .ToListAsync(ct);

        var doomed = new HashSet<Guid>();

        // (a) EXACT id linkage — the only path that needs no text at all.
        foreach (var p in active.Where(p => p.EvidenceMessageId is { } mid && evidenceMessageIds.Contains(mid)))
            doomed.Add(p.Id);

        // (b) The text-only /forget flow. Candidates are resolved SEPARATELY and matched by
        // normalized exact equality — never containment, so an unrelated memory that merely
        // shares a phrase with an instruction cannot revoke it. A statement matching more
        // than one active record is ambiguous: it revokes NOTHING and is reported, because
        // silently picking one of two identical instructions would be a guess.
        var ambiguous = 0;
        foreach (var statement in forgottenStatements.Select(Normalize).Where(t => t.Length > 0).Distinct())
        {
            var candidates = active
                .Where(p => p.EvidenceStatement is { } stmt
                            && string.Equals(Normalize(stmt), statement, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1)
                doomed.Add(candidates[0].Id);
            else if (candidates.Count > 1)
                ambiguous++;
        }

        var invalidated = 0;
        foreach (var p in active.Where(p => doomed.Contains(p.Id)))
        {
            Invalidate(p, now);
            invalidated++;
        }

        if (invalidated > 0)
            await db.SaveChangesAsync(ct);
        return new PreferenceInvalidationResult(invalidated, ambiguous);
    }

    public async Task<int> InvalidateByEvidenceEventAsync(
        string userId, Guid evidenceEventId, DateTimeOffset now, CancellationToken ct = default)
    {
        var affected = await db.UserPreferences
            .Where(p => p.UserId == userId
                        && p.Status == UserPreferenceStatus.Active
                        && p.EvidenceEventId == evidenceEventId)
            .ToListAsync(ct);

        foreach (var p in affected)
            Invalidate(p, now);

        if (affected.Count > 0)
            await db.SaveChangesAsync(ct);
        return affected.Count;
    }

    private static void Invalidate(UserPreferenceRecord p, DateTimeOffset now)
    {
        p.Status = UserPreferenceStatus.EvidenceForgotten;
        p.DeactivatedAt = now;
        // The authority is gone WITH its text: a forgotten statement must not linger
        // in this table as a searchable copy of what the user asked to forget.
        p.EvidenceStatement = null;
        p.ActiveSlot = null;
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Trim();

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
