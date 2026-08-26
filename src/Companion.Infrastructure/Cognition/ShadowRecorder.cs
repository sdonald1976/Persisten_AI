using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// The answer when shadow mode is off, which is the default and most of the time.
///
/// Reporting <see cref="IsRecording"/> false is the useful part: callers skip running a model whose
/// answer they would discard, so switching shadow mode off costs nothing rather than costing an
/// inference per turn that nobody reads.
/// </summary>
internal sealed class NullShadowRecorder : IShadowRecorder
{
    public bool IsRecording => false;

    public bool IsShadowing => false;

    public Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
        DateTimeOffset since, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShadowAgreement>>(Array.Empty<ShadowAgreement>());

    public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
        string? subject, int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShadowComparison>>(Array.Empty<ShadowComparison>());

    public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
        string? subject, int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShadowComparison>>(Array.Empty<ShadowComparison>());

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        Guid? memoryId = null, CancellationToken ct = default)
        => Task.FromResult(0);
}

/// <summary>
/// Persists shadow comparisons alongside the rest of the operational telemetry.
///
/// Writes never throw into the caller. A comparison that fails to save has cost us a data point;
/// a comparison that takes down a turn has cost the user their conversation, and the two are not
/// close. Same guarantee as <see cref="IDiagnosticsStore"/>, for the same reason.
/// </summary>
internal sealed class ShadowRecorder : IShadowRecorder
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShadowRecorder> _logger;
    private readonly bool _shadowing;

    public ShadowRecorder(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<ShadowRecorder> logger,
        bool shadowing = true)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _shadowing = shadowing;
    }

    public bool IsRecording => true;

    /// <summary>Whether models run. False when this recorder exists only to serve capture.</summary>
    public bool IsShadowing => _shadowing;

    public async Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            comparison.Id = comparison.Id == Guid.Empty ? Guid.NewGuid() : comparison.Id;
            comparison.Timestamp = _clock.GetUtcNow();
            db.ShadowComparisons.Add(comparison);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record a shadow comparison for {Subject}.", comparison.Subject);
        }
    }

    public async Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var rows = await db.ShadowComparisons
            .AsNoTracking()
            .Where(c => c.Timestamp >= since)

            // Captures carry no model answer, so they cannot have agreed with one. Counting them
            // would report a rising agreement rate for a model that was never asked.
            .Where(c => c.Model != null)
            .GroupBy(c => c.Subject)
            .Select(g => new
            {
                Subject = g.Key,
                Comparisons = g.Count(),
                Disagreements = g.Count(c => !c.Agreed),
                Confidence = g.Average(c => c.Confidence),
                Duration = g.Average(c => (double)c.DurationMs),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ShadowAgreement
            {
                Subject = r.Subject,
                Comparisons = r.Comparisons,
                Disagreements = r.Disagreements,
                AverageConfidence = r.Confidence,
                AverageDurationMs = r.Duration,
            })
            .OrderByDescending(r => r.Disagreements)
            .ToList();
    }

    public async Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
        string? subject, int count, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var query = db.ShadowComparisons.AsNoTracking().Where(c => c.Model != null && !c.Agreed);
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(c => c.Subject == subject);

        return await query
            .OrderByDescending(c => c.Timestamp)
            .Take(Math.Clamp(count, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
        string? subject, int count, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var query = db.ShadowComparisons.AsNoTracking().Where(c => c.Model == null);
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(c => c.Subject == subject);

        // A larger cap than the disagreement queue, because this one is read to be exported rather
        // than to be looked at: a review queue is a page of rows, a corpus is all of them.
        return await query
            .OrderByDescending(c => c.Timestamp)
            .Take(Math.Clamp(count, 1, 5000))
            .ToListAsync(ct);
    }

    /// <summary>
    /// A capture row has to be reachable by "the user asked me to forget the thing this sentence
    /// said", and the only handle it carries is the sentence. So the memory's own evidence excerpts
    /// are matched against the stored text.
    ///
    /// Short excerpts are skipped rather than matched loosely. An excerpt of "the roof" would match
    /// every sentence mentioning a roof, and deleting more than was asked for is a worse failure
    /// here than deleting less: the row is training data, the alternative to removing it is a
    /// second pass by a human, and there is no way to get a wrongly deleted one back.
    /// </summary>
    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        Guid? memoryId = null, CancellationToken ct = default)
    {
        if (messageIds.Count == 0 && memoryId is null)
            return 0;

        var ids = messageIds.ToHashSet();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

            // Ownership AND exact evidence, both required, both in the query. A row with no
            // UserId or no SourceMessageId predates A3 and can never be attributed to a turn,
            // so it is never matched here -- those rows were purged by the migration instead,
            // because inventing ownership for them would delete somebody else's diagnostics.
            var doomed = await db.ShadowComparisons
                .Where(c => c.UserId == userId
                            && (c.SourceMessageId != null || c.SourceMemoryId != null))
                .ToListAsync(ct);
            doomed = doomed
                .Where(c => (c.SourceMessageId is { } m && ids.Contains(m))
                            // Pair rows are about a stored MEMORY, and its id is the only
                            // handle that finds them.
                            || (memoryId is { } mem && c.SourceMemoryId == mem))
                .ToList();
            if (doomed.Count == 0)
                return 0;

            // Deleted, not redacted. A shadow comparison is diagnostic rather than
            // authoritative, and its whole substance is the text it quotes: redacting it
            // leaves a row that says nothing and costs the same to keep.
            db.ShadowComparisons.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            return doomed.Count;
        }
        catch (Exception ex)
        {
            // Unlike a failed write, a failed FORGET is not survivable silence.
            _logger.LogError(ex, "Could not forget shadow comparisons for a user.");
            throw;
        }
    }

    /// <summary>
    /// Below this an excerpt is too generic to match on. Chosen to exclude the short noun phrases
    /// evidence often quotes ("the roof", "the shed") while keeping whole clauses.
    /// </summary>

    /// <summary>
    /// A3. Age-based retention. Shadow comparisons had none at all, so verbatim messages and
    /// replies accumulated indefinitely in a table whose whole purpose is short-lived
    /// evaluation. Separate from forgetting: this sweeps by age and knows nothing about
    /// evidence, and forgetting reaches rows this has not yet aged out.
    ///
    /// A terminal evaluation artifact that must outlive this window is exported deliberately,
    /// without private conversation content -- it is not kept here by leaving the table
    /// unbounded.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            return await db.ShadowComparisons
                .Where(c => c.Timestamp < olderThan)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prune shadow comparisons.");
            return 0;
        }
    }
}
