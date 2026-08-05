using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Text;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Resolves a reference to a project by scoring each candidate on alias match, name/keyword
/// overlap, embedding similarity, and recency. It picks a project only when one clearly wins;
/// when the top two are close it asks for clarification instead of guessing. Never silently
/// merges uncertain candidates.
/// </summary>
public sealed class EntityResolver : IEntityResolver
{
    // Fixed signal weights — resolution is identity matching, kept simple and legible.
    private const double AliasWeight = 1.0;
    private const double NameWeight = 0.6;
    private const double EmbeddingWeight = 0.7;
    private const double RecencyWeight = 0.2;
    private const double RecencyHalfLifeDays = 120.0;

    private readonly IProjectStore _projects;
    private readonly IEmbeddingModel _embeddings;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;

    public EntityResolver(
        IProjectStore projects,
        IEmbeddingModel embeddings,
        IOptions<CompanionOptions> options,
        TimeProvider clock)
    {
        _projects = projects;
        _embeddings = embeddings;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<EntityResolution> ResolveProjectAsync(
        string userId, string reference, CancellationToken ct = default)
    {
        var projects = await _projects.GetProjectsAsync(userId, ct);
        if (projects.Count == 0 || string.IsNullOrWhiteSpace(reference))
            return EntityResolution.None;

        var aliases = (await _projects.GetAliasesAsync(userId, ct))
            .GroupBy(a => a.ProjectId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Alias).ToList());

        var referenceEmbedding = await _embeddings.EmbedAsync(reference, ct);
        var referenceTokens = new HashSet<string>(Tokenizer.Tokenize(reference));
        var now = _clock.GetUtcNow();

        // A project qualifies only on IDENTITY evidence (alias/name/embedding); recency alone
        // must never make an unrelated project a candidate. Recency then ranks real matches.
        var ranked = projects
            .Select(p => Score(p, aliases.GetValueOrDefault(p.Id, new List<string>()),
                reference, referenceTokens, referenceEmbedding, now))
            .Where(m => IdentityScore(m) >= _options.ResolutionMinScore)
            .OrderByDescending(m => m.Score)
            .ToList();

        if (ranked.Count == 0)
            return EntityResolution.None;

        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1] : null;

        // Margin-based confidence: how much the leader dominates the runner-up.
        var relativeConfidence = second is null
            ? 1.0
            : top.Score / (top.Score + second.Score);

        var withConfidence = ranked
            .Select(m => m with { Confidence = ranked[0].Score <= 0 ? 0 : m.Score / (top.Score + (second?.Score ?? 0)) })
            .ToList();

        var confidentTop = withConfidence[0];

        // Confident pick: clear leader.
        if (second is null || relativeConfidence >= _options.ResolutionConfidenceThreshold)
        {
            return new EntityResolution
            {
                Candidates = withConfidence,
                Best = confidentTop,
                RequiresClarification = false,
            };
        }

        // Ambiguous: two viable candidates too close to call → ask.
        return new EntityResolution
        {
            Candidates = withConfidence,
            Best = null,
            RequiresClarification = true,
            ClarificationQuestion = BuildClarification(withConfidence),
        };
    }

    private static double IdentityScore(ProjectMatch m)
        => m.Signals.GetValueOrDefault("alias") + m.Signals.GetValueOrDefault("name") + m.Signals.GetValueOrDefault("embedding");

    private ProjectMatch Score(
        Project project, List<string> aliases, string reference,
        HashSet<string> referenceTokens, float[] referenceEmbedding, DateTimeOffset now)
    {
        var referenceLower = reference.ToLowerInvariant();

        // Alias match: the project name counts as an implicit alias.
        var allNames = aliases.Append(project.Name);
        var aliasScore = allNames.Select(a => AliasScore(a, referenceLower, referenceTokens)).DefaultIfEmpty(0).Max();

        var nameScore = ScoreMath.KeywordOverlap(reference, $"{project.Name} {project.Purpose}");
        var embeddingScore = ScoreMath.Cosine(referenceEmbedding, project.Embedding);
        var recencyScore = ScoreMath.Recency(project.LastActivityAt, now, RecencyHalfLifeDays);

        var signals = new Dictionary<string, double>
        {
            ["alias"] = aliasScore * AliasWeight,
            ["name"] = nameScore * NameWeight,
            ["embedding"] = embeddingScore * EmbeddingWeight,
            ["recency"] = recencyScore * RecencyWeight,
        };

        return new ProjectMatch
        {
            Project = project,
            Score = signals.Values.Sum(),
            Confidence = 0, // filled in after ranking
            Reason = BuildReason(signals),
            Signals = signals,
        };
    }

    private static double AliasScore(string alias, string referenceLower, HashSet<string> referenceTokens)
    {
        var aliasLower = alias.ToLowerInvariant();
        // Strong signal when either string contains the other (e.g. "buoy" in "the buoy board").
        if (referenceLower.Contains(aliasLower) || aliasLower.Contains(referenceLower))
            return 1.0;
        return ScoreMath.KeywordOverlap(referenceLower, aliasLower);
    }

    private static string BuildReason(IReadOnlyDictionary<string, double> signals)
    {
        var top = signals
            .Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value)
            .Take(2)
            .Select(s => s.Key);
        var parts = top.ToList();
        return parts.Count == 0 ? "weak match" : $"matched on {string.Join(" + ", parts)}";
    }

    private static string BuildClarification(IReadOnlyList<ProjectMatch> candidates)
    {
        var names = candidates.Take(3).Select(c => $"\"{c.Project.Name}\"").ToList();
        var joined = names.Count == 2
            ? $"{names[0]} or {names[1]}"
            : $"{string.Join(", ", names.Take(names.Count - 1))}, or {names[^1]}";
        return $"Do you mean {joined}?";
    }
}
