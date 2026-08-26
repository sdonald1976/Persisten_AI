using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// Singleton telemetry store. Writers are singletons (the model decorators), the DbContext is
/// scoped, so every operation opens its own scope. All writes swallow their own failures —
/// telemetry must never break the call it describes.
/// </summary>
public sealed class DiagnosticsStore : IDiagnosticsStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DiagnosticsStore> _logger;

    public DiagnosticsStore(IServiceScopeFactory scopes, ILogger<DiagnosticsStore> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task RecordModelCallAsync(ModelCallRecord record, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            db.ModelCalls.Add(record);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to record a model call ({Role}); the call itself is unaffected.", record.Role);
        }
    }

    public async Task RecordToolCallAsync(ToolCallRecord record, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            db.ToolCalls.Add(record);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to record a tool call ({Tool}); the call itself is unaffected.", record.Tool);
        }
    }

    public async Task<IReadOnlyList<ToolCallRecord>> GetRecentToolCallsAsync(
        string userId, int count, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.ToolCalls.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.Timestamp)
            .Take(Math.Clamp(count, 1, 200))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ModelRoleStats>> GetModelStatsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var calls = await db.ModelCalls.AsNoTracking()
            .Where(c => c.Timestamp >= since)
            .ToListAsync(ct);

        return calls
            .GroupBy(c => (c.Role, c.Model))
            .Select(g => new ModelRoleStats
            {
                Role = g.Key.Role,
                Model = g.Key.Model,
                Calls = g.Count(),
                Failures = g.Count(c => !c.Ok),
                AvgDurationMs = Math.Round(g.Average(c => (double)c.DurationMs), 1),
                PromptTokens = g.Sum(c => (long)(c.PromptTokens ?? 0)),
                CompletionTokens = g.Sum(c => (long)(c.CompletionTokens ?? 0)),
                LastCalledAt = g.Max(c => c.Timestamp),
            })
            .OrderBy(s => s.Role).ThenBy(s => s.Model)
            .ToList();
    }

    public async Task RecordTurnAsync(TurnRecord record, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
            db.TurnRecords.Add(record);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to record turn {TraceId}; the turn itself is unaffected.", record.Id);
        }
    }

    public async Task<IReadOnlyList<TurnRecord>> GetRecentTurnsAsync(
        string userId, int count, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.TurnRecords.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.Timestamp)
            .Take(Math.Clamp(count, 1, 200))
            .ToListAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var models = await db.ModelCalls.Where(c => c.Timestamp < olderThan).ExecuteDeleteAsync(ct);
        var tools = await db.ToolCalls.Where(t => t.Timestamp < olderThan).ExecuteDeleteAsync(ct);
        var turnRows = await db.TurnRecords.Where(t => t.Timestamp < olderThan).ExecuteDeleteAsync(ct);
        return models + tools + turnRows;
    }

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return 0;
        var ids = messageIds.ToHashSet();

        // Unlike every other write here, this one is NOT allowed to swallow its failure: a
        // silently-failed telemetry write costs a data point, a silently-failed forget costs
        // the promise.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

        var rows = await db.TurnRecords
            .Where(t => t.UserId == userId && t.SourceMessageId != null)
            .ToListAsync(ct);

        var n = EvidenceForgetting.ForgetTurnRecords(rows, ids);
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }
}
