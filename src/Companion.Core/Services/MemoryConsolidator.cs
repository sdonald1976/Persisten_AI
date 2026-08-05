using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Rolls repeated, related low-level memories up into higher-level knowledge. It clusters
/// same-slot memories by topic similarity, and when a cluster is large enough it summarizes
/// them into one consolidated fact (<see cref="MemoryOrigin.Consolidated"/>) that keeps links to
/// all the supporting evidence. Originals are left untouched, and a single remark never
/// generalizes (min-observations guard). Re-running is idempotent per topic.
/// </summary>
public sealed class MemoryConsolidator : IMemoryConsolidator
{
    private readonly IMemoryStore _memories;
    private readonly ISummarizer _summarizer;
    private readonly IEmbeddingModel _embeddings;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MemoryConsolidator> _logger;

    public MemoryConsolidator(
        IMemoryStore memories,
        ISummarizer summarizer,
        IEmbeddingModel embeddings,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<MemoryConsolidator> logger)
    {
        _memories = memories;
        _summarizer = summarizer;
        _embeddings = embeddings;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ConsolidationResult> ConsolidateAsync(string userId, CancellationToken ct = default)
    {
        var active = (await _memories.GetRetrievableMemoriesAsync(userId, ct))
            .OfType<SemanticMemory>()
            .Where(m => m.Status == MemoryStatus.Active)
            .ToList();

        var sources = active.Where(m => m.Origin != MemoryOrigin.Consolidated).ToList();
        var existingConsolidated = active.Where(m => m.Origin == MemoryOrigin.Consolidated).ToList();

        var created = new List<ConsolidatedGroup>();

        // Work slot by slot (same subject+predicate = same category of fact).
        foreach (var slot in sources.GroupBy(m => MemoryNormalizer.SemanticSlotKey(m.Subject, m.Predicate)))
        {
            foreach (var cluster in ClusterByTopic(slot.ToList()))
            {
                if (cluster.Count < _options.ConsolidationMinObservations)
                    continue;

                // Idempotency: skip if we already have a consolidated memory for this topic.
                if (existingConsolidated.Any(c => ScoreMath.Cosine(c.Embedding, cluster[0].Embedding) >= _options.ConsolidationMinSimilarity
                        && MemoryNormalizer.SemanticSlotKey(c.Subject, c.Predicate) == slot.Key))
                    continue;

                var group = await ConsolidateClusterAsync(userId, cluster, ct);
                created.Add(group);
            }
        }

        if (created.Count > 0)
            _logger.LogInformation("Consolidated {Count} memory groups for {User}", created.Count, userId);

        return new ConsolidationResult { Created = created };
    }

    /// <summary>Greedy topic clustering within a slot: seed a cluster, pull in everything similar to the seed.</summary>
    private IEnumerable<List<SemanticMemory>> ClusterByTopic(List<SemanticMemory> members)
    {
        var remaining = new List<SemanticMemory>(members);
        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);

            var cluster = new List<SemanticMemory> { seed };
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                if (ScoreMath.Cosine(seed.Embedding, remaining[i].Embedding) >= _options.ConsolidationMinSimilarity)
                {
                    cluster.Add(remaining[i]);
                    remaining.RemoveAt(i);
                }
            }
            yield return cluster;
        }
    }

    private async Task<ConsolidatedGroup> ConsolidateClusterAsync(
        string userId, List<SemanticMemory> cluster, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var summary = await _summarizer.SummarizeAsync(cluster.Select(m => m.NormalizedFact).ToList(), ct);

        // Union the supporting evidence so provenance points back to every source message.
        var evidence = new List<MemoryEvidence>();
        var seenMessages = new HashSet<Guid>();
        foreach (var member in cluster)
        {
            foreach (var e in await _memories.GetEvidenceAsync(member.Id, ct))
            {
                if (seenMessages.Add(e.MessageId))
                    evidence.Add(new MemoryEvidence { MessageId = e.MessageId, Excerpt = e.Excerpt, Weight = e.Weight });
            }
        }

        var consolidated = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = cluster[0].Subject,
            Predicate = cluster[0].Predicate,
            Value = summary,
            NormalizedFact = summary,
            Origin = MemoryOrigin.Consolidated,
            Status = MemoryStatus.Active,
            Validity = Validity.Current,
            Confidence = ConfidenceCalculator.Compute(
                cluster.Max(m => m.Confidence), fromDirectUserStatement: false, corroborations: cluster.Count),
            Importance = cluster.Max(m => m.Importance),
            FirstObserved = cluster.Min(m => m.FirstObserved),
            LastConfirmed = cluster.Max(m => m.LastConfirmed),
            CreatedAt = now,
            Embedding = await _embeddings.EmbedAsync(summary, ct),
        };
        foreach (var e in evidence)
        {
            e.Id = Guid.NewGuid();
            e.MemoryId = consolidated.Id;
            e.MemoryKind = MemoryKind.Semantic;
            consolidated.Evidence.Add(e);
        }

        await _memories.AddSemanticAsync(consolidated, ct);
        await _memories.AddRevisionAsync(new MemoryRevision
        {
            Id = Guid.NewGuid(),
            MemoryId = consolidated.Id,
            MemoryKind = MemoryKind.Semantic,
            Kind = RevisionKind.Created,
            Timestamp = now,
            Actor = "consolidation",
            Note = $"Consolidated from {cluster.Count} memories.",
            After = string.Join(" | ", cluster.Select(m => m.Id)),
        }, ct);

        return new ConsolidatedGroup
        {
            ConsolidatedMemoryId = consolidated.Id,
            Content = summary,
            SourceMemoryIds = cluster.Select(m => m.Id).ToList(),
            EvidenceCount = consolidated.Evidence.Count,
        };
    }
}
