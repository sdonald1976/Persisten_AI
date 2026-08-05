using Companion.Core.Text;

namespace Companion.Core.Services;

/// <summary>
/// Pure scoring helpers with no I/O — unit-tested in isolation. Cosine similarity,
/// time-decayed recency, keyword (Jaccard) overlap, and a weighted-sum combiner.
/// </summary>
public static class ScoreMath
{
    /// <summary>Cosine similarity of two vectors, clamped to [0, 1]; 0 if either is empty/degenerate.</summary>
    public static double Cosine(IReadOnlyList<float>? a, IReadOnlyList<float>? b)
    {
        if (a is null || b is null || a.Count == 0 || a.Count != b.Count)
            return 0.0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na <= 0 || nb <= 0)
            return 0.0;

        var sim = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return Math.Clamp(sim, 0.0, 1.0);
    }

    /// <summary>
    /// Exponential recency decay in [0, 1]. Score 1 at age 0, halving every
    /// <paramref name="halfLifeDays"/>. Never negative even for future timestamps.
    /// </summary>
    public static double Recency(DateTimeOffset effectiveAt, DateTimeOffset now, double halfLifeDays)
    {
        if (halfLifeDays <= 0)
            return 1.0;

        var ageDays = Math.Max(0.0, (now - effectiveAt).TotalDays);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    /// <summary>Jaccard overlap of the stemmed token sets of two texts, in [0, 1].</summary>
    public static double KeywordOverlap(string? query, string? candidate)
    {
        var q = new HashSet<string>(Tokenizer.Tokenize(query));
        var c = new HashSet<string>(Tokenizer.Tokenize(candidate));
        if (q.Count == 0 || c.Count == 0)
            return 0.0;

        var intersection = q.Count(c.Contains);
        var union = q.Count + c.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>Sum of already-weighted signal contributions.</summary>
    public static double Combine(IReadOnlyDictionary<string, double> weightedSignals)
        => weightedSignals.Values.Sum();
}
