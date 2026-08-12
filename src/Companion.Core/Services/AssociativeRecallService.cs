using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

public sealed class AssociativeRecallService : IAssociativeRecallService
{
    private readonly IMemoryAssociationStore _associations;
    private readonly IMemoryStore _memories;
    private readonly CompanionOptions _options;

    public AssociativeRecallService(
        IMemoryAssociationStore associations, IMemoryStore memories, IOptions<CompanionOptions> options)
    {
        _associations = associations;
        _memories = memories;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<RetrievalResult>> ExpandAsync(
        string userId, string query, IReadOnlyList<RetrievalResult> primary, int limit, CancellationToken ct = default)
    {
        var ids = primary.Select(r => r.Memory.Id).ToHashSet();
        var associations = await _associations.GetFromSourcesAsync(userId, ids, ct);
        var memories = await _memories.GetRetrievalCandidatesAsync(userId, ct);
        var byId = memories.ToDictionary(m => m.Id);

        return associations
            .Where(a => !ids.Contains(a.TargetMemoryId) && byId.ContainsKey(a.TargetMemoryId))
            .Select(a =>
            {
                var memory = byId[a.TargetMemoryId];
                var relevance = ScoreMath.KeywordOverlap(query, memory.Content);
                return (Association: a, Memory: memory, Relevance: relevance, Score: a.Strength + relevance);
            })
            .Where(x => x.Association.Strength >= _options.AssociativeMinStrength || x.Relevance >= _options.RelevanceFloor)
            .OrderByDescending(x => x.Score)
            .Take(Math.Min(limit, _options.MaxAssociativeMemories))
            .Select(x => new RetrievalResult
            {
                Memory = x.Memory,
                Score = x.Score,
                Signals = new Dictionary<string, double>
                {
                    ["associationStrength"] = x.Association.Strength,
                    ["keywordOverlap"] = x.Relevance,
                },
                Reason = $"associated via {x.Association.AssociationType}: {x.Association.Evidence}",
                Source = RetrievalSource.Associative,
            })
            .ToList();
    }
}
