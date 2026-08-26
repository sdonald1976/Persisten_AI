using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

public sealed record DuplicateVerdict(bool IsDuplicate, string? Code, string? Against);

/// <summary>
/// Deduplication and diversity: the difference between a corpus and fifty thousand copies of
/// "Sure! I'd be happy to help."
///
/// Exact duplicates go first, then near-duplicates by token Jaccard, then contiguous-run overlap
/// against sources (R5 §"longest common contiguous token run vs source ≤ 7 tokens" — the check
/// that keeps distilled rows from being quotations).
/// </summary>
public sealed class Deduplicator(double nearDuplicateThreshold = 0.85, int maxContiguousRun = 7)
{
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Id, HashSet<string> Tokens)> _seen = [];

    public DuplicateVerdict Check(string id, string target)
    {
        var normalized = Normalize(target);
        if (!_exact.Add(normalized))
            return new DuplicateVerdict(true, "exact-duplicate", null);

        var tokens = Tokenize(target);
        foreach (var (otherId, otherTokens) in _seen)
        {
            if (Jaccard(tokens, otherTokens) >= nearDuplicateThreshold)
                return new DuplicateVerdict(true, "near-duplicate", otherId);
        }

        _seen.Add((id, tokens));
        return new DuplicateVerdict(false, null, null);
    }

    /// <summary>
    /// Longest run of consecutive tokens shared with a source text. A distilled row that quotes
    /// its source is not distilled, and it carries the source's licence with it.
    /// </summary>
    public bool QuotesSource(string target, string sourceText, out int longestRun)
    {
        var a = Tokenize(target).ToList();
        var b = Tokenize(sourceText).ToList();
        var at = target.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bt = sourceText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        longestRun = 0;
        var table = new int[at.Length + 1, bt.Length + 1];
        for (var i = 1; i <= at.Length; i++)
        for (var j = 1; j <= bt.Length; j++)
        {
            if (at[i - 1] != bt[j - 1])
                continue;
            table[i, j] = table[i - 1, j - 1] + 1;
            if (table[i, j] > longestRun)
                longestRun = table[i, j];
        }
        _ = a; _ = b;
        return longestRun > maxContiguousRun;
    }

    private static string Normalize(string text)
        => string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> Tokenize(string text)
        => new(text.ToLowerInvariant()
            .Split([' ', '\n', '\t', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }
}

public sealed record DistributionReport
{
    public required int Total { get; init; }
    public required IReadOnlyDictionary<string, int> ByFamily { get; init; }
    public required IReadOnlyDictionary<string, int> ByLayer { get; init; }
    public required IReadOnlyDictionary<string, int> BySourceFamily { get; init; }
    public required IReadOnlyDictionary<string, int> ByContextBucket { get; init; }
    public required IReadOnlyDictionary<string, int> TopOpenings { get; init; }

    /// <summary>Share of rows whose opening is shared with at least one other row.</summary>
    public required double RepeatedOpeningShare { get; init; }

    public required IReadOnlyDictionary<string, int> BySplit { get; init; }
}

public static class Distribution
{
    /// <summary>R5 §2's A7b buckets. Reported for every family so the sequence-length decision has data.</summary>
    public static string ContextBucket(int transcriptTurns) => transcriptTurns switch
    {
        <= 4 => "short (2-4)",
        <= 8 => "medium (5-8)",
        <= 16 => "long (9-16)",
        _ => "very long (17+)",
    };

    public static DistributionReport Build(IReadOnlyList<TrainingRowMetadata> rows)
    {
        var openings = rows
            .GroupBy(r => r.Opening, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var repeated = openings.Where(kv => kv.Value > 1).Sum(kv => kv.Value);

        return new DistributionReport
        {
            Total = rows.Count,
            ByFamily = Count(rows, r => r.FamilyId),
            ByLayer = Count(rows, r => r.Layer.ToString()),
            BySourceFamily = Count(rows, r => r.SourceFamilyId),
            ByContextBucket = Count(rows, r => ContextBucket(r.TranscriptTurns)),
            TopOpenings = openings.OrderByDescending(kv => kv.Value).Take(15)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            RepeatedOpeningShare = rows.Count == 0 ? 0 : (double)repeated / rows.Count,
            BySplit = Count(rows, r => r.Split ?? "(unassigned)"),
        };
    }

    private static Dictionary<string, int> Count(
        IReadOnlyList<TrainingRowMetadata> rows, Func<TrainingRowMetadata, string> key)
        => rows.GroupBy(key, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
