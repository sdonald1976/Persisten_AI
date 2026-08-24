using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF-backed shadow activity store. Every write is a transaction with optimistic
/// concurrency on <see cref="ActivityBranchRecord.Version"/>; duplicate idempotency keys
/// return the existing row without applying twice. Retention is enforced HERE, at the
/// persistence boundary, so a volatile branch cannot be quietly durably stored.
/// </summary>
internal sealed class ActivityBranchStore(
    IServiceScopeFactory scopes, ILogger<ActivityBranchStore> logger) : IActivityBranchStore
{
    public async Task<BranchWriteResult> UpsertAsync(
        ActivityBranchRecord record, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.ActivityBranches
            .FirstOrDefaultAsync(b => b.BranchId == record.BranchId, ct);

        if (existing is not null)
        {
            var applied = JsonSerializer.Deserialize<List<string>>(existing.AppliedKeysJson) ?? [];
            if (applied.Contains(idempotencyKey))
            {
                await tx.RollbackAsync(ct);
                return BranchWriteResult.AlreadyApplied(existing);
            }
            if (existing.Version != record.Version)
            {
                await tx.RollbackAsync(ct);
                return BranchWriteResult.Conflicted(existing,
                    $"version {record.Version} lost to {existing.Version}");
            }

            applied.Add(idempotencyKey);
            Apply(existing, record, applied);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return BranchWriteResult.Wrote(existing);
        }

        var fresh = Redact(record);
        fresh.Id = fresh.Id == Guid.Empty ? Guid.NewGuid() : fresh.Id;
        fresh.Version = 1;
        fresh.AppliedKeysJson = JsonSerializer.Serialize(new[] { idempotencyKey });
        db.ActivityBranches.Add(fresh);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BranchWriteResult.Wrote(fresh);
    }

    public async Task<ActivityBranchRecord?> GetAsync(string branchId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.ActivityBranches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BranchId == branchId, ct);
    }

    public async Task<IReadOnlyList<ActivityBranchRecord>> GetForConversationAsync(
        string userId, Guid conversationId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.ActivityBranches.AsNoTracking()
            .Where(b => b.UserId == userId && b.ConversationId == conversationId)
            .OrderByDescending(b => b.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CleanupAsync(
        DateTimeOffset now, TimeSpan terminalAge, TimeSpan volatileAge, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var terminalCutoff = now - terminalAge;
        var volatileCutoff = now - volatileAge;

        var doomed = await db.ActivityBranches
            .Where(b => (b.TerminalAt != null && b.TerminalAt < terminalCutoff)
                        || (b.Retention == "volatile_turn_only" && b.UpdatedAt < volatileCutoff))
            .ToListAsync(ct);
        if (doomed.Count == 0)
            return 0;

        db.ActivityBranches.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Cleaned up {Count} shadow activity branches.", doomed.Count);
        return doomed.Count;
    }

    public async Task<int> ForgetAsync(
        IReadOnlyCollection<string> excerpts, CancellationToken ct = default)
    {
        var usable = excerpts
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Trim().Length >= 12)
            .Select(e => e.Trim())
            .ToList();
        if (usable.Count == 0)
            return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var candidates = await db.ActivityBranches.ToListAsync(ct);
        var doomed = candidates.Where(b => usable.Any(e =>
            (b.MovesJson?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false)
            || (b.HypothesesJson?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false)
            || (b.FinalGuess?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false)
            || (b.ActivationEvidence?.Contains(e, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        if (doomed.Count == 0)
            return 0;

        db.ActivityBranches.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        return doomed.Count;
    }

    /// <summary>
    /// Retention at the persistence boundary (§5): volatile content is NOT written. The row
    /// keeps its metadata so diagnostics still work and marks ContentWithheld, which is how
    /// restart-resume is diagnosed as unavailable rather than silently downgraded.
    /// Subject matter is irrelevant here — only classification and disclosure decide.
    /// </summary>
    private static ActivityBranchRecord Redact(ActivityBranchRecord r)
    {
        if (r.Retention != "volatile_turn_only")
            return r;
        r.MovesJson = "[]";
        r.HypothesesJson = null;
        r.FinalGuess = null;
        r.ActivationEvidence = null;
        r.AnswerBindingsJson = "[]";
        r.ContentWithheld = true;
        return r;
    }

    private static void Apply(ActivityBranchRecord target, ActivityBranchRecord source, List<string> appliedKeys)
    {
        var redacted = Redact(source);
        target.Lifecycle = redacted.Lifecycle;
        target.CurrentQuestionNumber = redacted.CurrentQuestionNumber;
        target.MovesJson = redacted.MovesJson;
        target.AnswerBindingsJson = redacted.AnswerBindingsJson;
        target.HypothesesJson = redacted.HypothesesJson;
        target.FinalGuess = redacted.FinalGuess;
        target.FinalGuessCorrect = redacted.FinalGuessCorrect;
        target.ContentWithheld = redacted.ContentWithheld;
        target.UpdatedAt = redacted.UpdatedAt;
        target.TerminalAt = redacted.TerminalAt;
        target.AppliedKeysJson = JsonSerializer.Serialize(appliedKeys);
        target.Version++;
    }
}
