using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Hybrid, explainable retrieval. Each candidate memory is scored on several signals
/// (semantic similarity, keyword overlap, recency, importance, confidence, project
/// association, open-loop boost), each weighted and recorded so the result can explain
/// itself. Returns only the top-K above a minimum score — never a full dump.
/// </summary>
public sealed class Retriever : IRetriever
{
    private readonly IMemoryStore _memories;
    private readonly IEmbeddingModel _embeddings;
    private readonly IVectorIndex _vectorIndex;
    private readonly IMemoryReranker _reranker;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Retriever> _logger;

    public Retriever(
        IMemoryStore memories,
        IEmbeddingModel embeddings,
        IVectorIndex vectorIndex,
        IMemoryReranker reranker,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<Retriever> logger)
    {
        _memories = memories;
        _embeddings = embeddings;
        _vectorIndex = vectorIndex;
        _reranker = reranker;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RetrievalOutcome> RetrieveAsync(
        string userId, string query, string? detectedProject = null, CancellationToken ct = default)
    {
        // The query embedding is computed unconditionally: even with no memories yet, the turn
        // still exposes it (RetrievalOutcome.QueryEmbedding) for other similarity lookups
        // (e.g. resurfacing a relevant past musing).
        //
        // An unreachable embedding server must NOT cost the user their conversation: degrade to
        // keyword/recency retrieval for this turn (Cosine treats an empty vector as 0 similarity)
        // and let the reply happen. Memory is poorer for one turn; silence would be worse.
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddings.EmbedAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Embedding unavailable; retrieving on keywords alone this turn.");
            queryEmbedding = Array.Empty<float>();
        }

        var candidates = await _memories.GetRetrievableMemoriesAsync(userId, ct);
        if (candidates.Count == 0)
        {
            return new RetrievalOutcome
            {
                Selected = Array.Empty<RetrievalResult>(),
                Excluded = Array.Empty<RetrievalResult>(),
                QueryEmbedding = queryEmbedding,
            };
        }

        // Similarity comes through the vector-index seam. Ask for all candidates so every
        // memory gets a similarity value; missing ids fall back to 0. With no query embedding
        // (server down) the search is skipped entirely and every similarity is 0.
        var hits = queryEmbedding.Length == 0
            ? Array.Empty<VectorHit>()
            : await _vectorIndex.SearchAsync(userId, queryEmbedding, candidates.Count, ct);
        var similarity = hits.ToDictionary(h => h.MemoryId, h => h.Similarity);

        var now = _clock.GetUtcNow();
        var weights = _options.Weights;

        var scored = new List<RetrievalResult>(candidates.Count);
        // Raw topical relevance per memory (similarity + keyword + project), before recency/importance
        // weighting. A memory must clear the RelevanceFloor on THIS to be admitted, so a merely
        // recent or important fact can't bleed into an unrelated turn.
        var relevance = new Dictionary<Guid, double>(candidates.Count);
        foreach (var memory in candidates)
        {
            var sim = similarity.GetValueOrDefault(memory.Id, 0.0);
            var kw = ScoreMath.KeywordOverlap(query, memory.Content);
            var rec = ScoreMath.Recency(memory.EffectiveAt, now, _options.RecencyHalfLifeDays);
            var imp = Math.Clamp(memory.Importance, 0.0, 1.0);
            var conf = Math.Clamp(memory.Confidence, 0.0, 1.0);

            var projectMatch = detectedProject is not null
                && string.Equals(memory.RelatedProject, detectedProject, StringComparison.OrdinalIgnoreCase);

            // An open loop is only boosted when the turn is already meaningfully relevant to it
            // (a real keyword/semantic hit or a project match), so unresolved items surface in
            // context rather than nagging — and mock-embedding noise doesn't trigger the boost.
            var baseRelevance = sim + kw + (projectMatch ? 1.0 : 0.0);
            relevance[memory.Id] = baseRelevance;
            var isOpenLoop = memory is EpisodicMemory { IsOpenLoop: true };
            var applyOpenLoop = isOpenLoop && baseRelevance >= _options.RelevanceFloor;

            var signals = new Dictionary<string, double>
            {
                ["semanticSimilarity"] = sim * weights.SemanticSimilarity,
                ["keywordOverlap"] = kw * weights.KeywordOverlap,
                ["recency"] = rec * weights.Recency,
                ["importance"] = imp * weights.Importance,
                ["confidence"] = conf * weights.Confidence,
                ["projectAssociation"] = (projectMatch ? 1.0 : 0.0) * weights.ProjectAssociation,
                ["openLoopBoost"] = (applyOpenLoop ? 1.0 : 0.0) * weights.OpenLoopBoost,
            };

            var score = ScoreMath.Combine(signals);
            scored.Add(new RetrievalResult
            {
                Memory = memory,
                Score = score,
                Signals = signals,
                Reason = BuildReason(signals, memory, projectMatch, applyOpenLoop),
            });
        }

        var ranked = scored.OrderByDescending(r => r.Score).ToList();
        var eligible = ranked
            .Where(r => r.Score >= _options.MinScore
                && relevance.GetValueOrDefault(r.Memory.Id) >= _options.RelevanceFloor)
            .ToList();
        var selected = (await _reranker.RerankAsync(query, eligible, _options.TopK, ct)).ToList();
        var excluded = ranked.Except(selected).ToList();

        _logger.LogDebug(
            "Retrieval for user {UserId}: {Selected}/{Total} selected, project={Project}",
            userId, selected.Count, candidates.Count, detectedProject ?? "(none)");

        return new RetrievalOutcome
        {
            Selected = selected,
            Excluded = excluded,
            QueryEmbedding = queryEmbedding,
        };
    }

    private static string BuildReason(
        IReadOnlyDictionary<string, double> signals, IMemory memory, bool projectMatch, bool openLoop)
    {
        var parts = new List<string>();

        var top = signals
            .Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value)
            .Take(2)
            .Select(s => Describe(s.Key));
        parts.AddRange(top);

        if (projectMatch && memory.RelatedProject is not null)
            parts.Add($"matches project '{memory.RelatedProject}'");
        if (openLoop)
            parts.Add("unresolved open loop");

        return parts.Count == 0 ? "weak match" : string.Join("; ", parts.Distinct());
    }

    private static string Describe(string signal) => signal switch
    {
        "semanticSimilarity" => "semantically similar",
        "keywordOverlap" => "shared keywords",
        "recency" => "recent",
        "importance" => "important",
        "confidence" => "high confidence",
        "projectAssociation" => "project match",
        "openLoopBoost" => "open loop",
        _ => signal,
    };
}
