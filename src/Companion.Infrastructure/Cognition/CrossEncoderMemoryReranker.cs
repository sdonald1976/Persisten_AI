using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Reorders retrieved memories with a cross-encoder, and falls back to the retriever's own ranking
/// whenever the model has nothing to say.
///
/// The third implementation of <see cref="IMemoryReranker"/>, beside the rule-based one (keep the
/// order) and the generative one (ask a 3B to score relevance). Having all three behind one
/// interface is the point: they can be swapped by configuration and compared on the same traffic,
/// which is the only way the choice between them is ever more than a preference.
///
/// It is <b>off by default, and deliberately so</b>. Measured on this project's own resolution set,
/// the cross-encoder does not beat <c>ScoreMath.KeywordOverlap</c>: 11/12 each, and a hybrid of the
/// two also 11/12. It gets right the case the keyword score structurally cannot — "the buoy one"
/// shares no token with "Marsh Lane marine sensor project", so Jaccard scores zero and the tie
/// falls to whatever was first — and it gets wrong one the keyword score gets right. A dozen rows
/// is far too few to conclude from either way, which is exactly why it stays off: the rule is that
/// a model replaces a heuristic on evidence, and "no measured difference" is not that evidence.
///
/// What it is for now is shadow mode, where it can be judged on real conversations instead of on
/// twelve rows somebody wrote down.
/// </summary>
public sealed class CrossEncoderMemoryReranker : IMemoryReranker
{
    private readonly ITextPairScorer _scorer;
    private readonly IMemoryReranker _fallback;
    private readonly ILogger<CrossEncoderMemoryReranker> _logger;

    public CrossEncoderMemoryReranker(
        ITextPairScorer scorer, IMemoryReranker fallback, ILogger<CrossEncoderMemoryReranker> logger)
    {
        _scorer = scorer;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query, IReadOnlyList<RetrievalResult> candidates, int maxResults,
        CancellationToken ct = default)
    {
        if (!_scorer.IsAvailable || candidates.Count <= 1)
            return await _fallback.RerankAsync(query, candidates, maxResults, ct);

        var scores = await _scorer.ScoreAsync(
            query, candidates.Select(c => c.Memory.Content).ToList(), ct);

        // A partial or absent answer is no answer. Reordering some of the list by one signal and
        // the rest by another produces a ranking that is neither, and the retriever's ordering is
        // explainable — every score in it can say which signal contributed what.
        if (scores.Count != candidates.Count)
        {
            _logger.LogDebug("Cross-encoder had no opinion on this turn; keeping the retriever's order.");
            return await _fallback.RerankAsync(query, candidates, maxResults, ct);
        }

        return candidates
            .Select((candidate, i) => (Candidate: candidate, Score: scores[i]))
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => x.Candidate)
            .ToList();
    }
}
