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

    public ShadowRecorder(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<ShadowRecorder> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    public bool IsRecording => true;

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
}
