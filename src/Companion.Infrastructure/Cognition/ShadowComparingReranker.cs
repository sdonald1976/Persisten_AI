using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Runs the cross-encoder and the deterministic rule reranker in SHADOW beside the authoritative
/// reranker, and records all three orderings — while returning the authoritative result and only
/// the authoritative result to the turn.
///
/// The isolation contract, in order of importance:
///
///  1. The authoritative reranker runs first and its result is what the turn gets. It is never
///     wrapped in the shadow's timeout, never affected by a shadow failure.
///  2. The shadow rerankers run AFTER the authoritative result is in hand, each under its own
///     short timeout, each in its own try/catch. A failure or timeout is recorded and the turn
///     continues — a broken cross-encoder can never delay or break a reply.
///  3. Recording is skipped entirely when the turn scope forbids it (private/sensitive turns) or
///     no scope is open. Nothing is written for those turns.
///  4. The record holds candidate IDS and orderings only — no memory or query text.
///
/// Shadow work is awaited (bounded by the per-method timeout) rather than fire-and-forget,
/// because the rerankers are cheap (rule is in-memory; the cross-encoder is ~25 ms CPU) and
/// awaiting keeps the record causally tied to this turn without a background queue. The total
/// added latency is bounded by <see cref="_shadowTimeout"/> per shadow method and is paid only
/// on turns where recording is enabled.
/// </summary>
public sealed class ShadowComparingReranker : IMemoryReranker
{
    private readonly IMemoryReranker _authoritative;
    private readonly IMemoryReranker _crossEncoder;
    private readonly IMemoryReranker _rule;
    private readonly IRerankShadowSink _sink;
    private readonly ILogger<ShadowComparingReranker> _logger;
    private readonly TimeSpan _shadowTimeout;

    public ShadowComparingReranker(
        IMemoryReranker authoritative, IMemoryReranker crossEncoder, IMemoryReranker rule,
        IRerankShadowSink sink, ILogger<ShadowComparingReranker> logger,
        TimeSpan? shadowTimeout = null)
    {
        _authoritative = authoritative;
        _crossEncoder = crossEncoder;
        _rule = rule;
        _sink = sink;
        _logger = logger;
        _shadowTimeout = shadowTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query, IReadOnlyList<RetrievalResult> candidates, int maxResults,
        CancellationToken ct = default)
    {
        // 1. Authoritative, unguarded, on the real path.
        var authWatch = Stopwatch.StartNew();
        var authoritative = await _authoritative.RerankAsync(query, candidates, maxResults, ct);
        authWatch.Stop();

        // 2. Shadow only when this turn permits recording and there is something to compare.
        if (!_sink.IsRecording || !RerankShadowScope.ShouldRecord || candidates.Count <= 1)
            return authoritative;

        var turn = RerankShadowScope.Turn;
        if (turn is null)
            return authoritative;

        // Everything below is best-effort. A throw here must not reach the caller: the
        // authoritative result is already computed and is what the turn will use.
        try
        {
            var ce = await RunShadowAsync("cross-encoder", _crossEncoder, query, candidates, maxResults);
            var rule = await RunShadowAsync("rule", _rule, query, candidates, maxResults);

            var record = new RerankShadowRecord
            {
                TurnId = turn.Value.TurnId,
                UserId = turn.Value.UserId,
                Timestamp = DateTimeOffset.UtcNow,
                CandidateIds = candidates.Select(c => c.Memory.Id).ToList(),
                CandidateSetHash = HashSet(candidates.Select(c => c.Memory.Id)),
                Authoritative = new RerankMethodResult
                {
                    Method = "authoritative-3b",
                    Ranking = authoritative.Select(r => r.Memory.Id).ToList(),
                    LatencyMs = authWatch.Elapsed.TotalMilliseconds,
                },
                CrossEncoder = ce,
                Rule = rule,
            };
            _sink.Record(record);
        }
        catch (Exception ex)
        {
            // The shadow failed to even assemble a record; note it and move on.
            _logger.LogWarning(ex, "Reranker shadow comparison failed to record; turn unaffected.");
        }

        return authoritative;
    }

    private async Task<RerankMethodResult> RunShadowAsync(
        string method, IMemoryReranker reranker, string query,
        IReadOnlyList<RetrievalResult> candidates, int maxResults)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(_shadowTimeout);
            var ranked = await reranker.RerankAsync(query, candidates, maxResults, cts.Token);
            watch.Stop();
            return new RerankMethodResult
            {
                Method = method,
                Ranking = ranked.Select(r => r.Memory.Id).ToList(),
                Scores = ranked
                    .Where(r => r.Signals.ContainsKey("rerank"))
                    .ToDictionary(r => r.Memory.Id, r => r.Signals["rerank"]),
                LatencyMs = watch.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            watch.Stop();
            _logger.LogDebug(ex, "Shadow reranker {Method} failed; recorded as a failure.", method);
            return new RerankMethodResult
            {
                Method = method,
                Ranking = [],
                LatencyMs = watch.Elapsed.TotalMilliseconds,
                Failed = true,
                FailureReason = ex is OperationCanceledException ? "timeout" : ex.GetType().Name,
            };
        }
    }

    /// <summary>Order-independent hash of a candidate set, so the same set groups together.</summary>
    private static string HashSet(IEnumerable<Guid> ids)
    {
        var joined = string.Join(",", ids.Select(i => i.ToString("N")).OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }
}
