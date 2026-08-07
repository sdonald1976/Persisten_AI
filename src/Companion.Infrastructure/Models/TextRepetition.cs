using Companion.Core.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Detects when a continuation round is just repeating text the reply already contains — the
/// failure mode where a small model, asked to "continue", loops the same paragraph. Used as a hard
/// stop so auto-continuation can never amplify a loop into a wall of duplicated text.
/// </summary>
internal static class TextRepetition
{
    private const int ShingleSize = 5;

    /// <summary>
    /// True if <paramref name="candidate"/> is largely already present in <paramref name="existing"/>.
    /// Empty/whitespace counts as a repeat (nothing new was added). Short candidates fall back to
    /// substring containment; longer ones use the fraction of their word 5-grams already seen.
    /// </summary>
    public static bool IsLargelyContained(string candidate, string existing, double threshold = 0.6)
    {
        var candWords = Tokenizer.Tokenize(candidate).ToList();
        if (candWords.Count == 0)
            return true;

        var existingNorm = Normalize(existing);
        if (candWords.Count < ShingleSize)
            return existingNorm.Contains(Normalize(candidate), StringComparison.Ordinal);

        var existingShingles = Shingles(Tokenizer.Tokenize(existing).ToList());
        if (existingShingles.Count == 0)
            return false;

        var candShingles = Shingles(candWords);
        var seen = candShingles.Count(existingShingles.Contains);
        return (double)seen / candShingles.Count >= threshold;
    }

    private static HashSet<string> Shingles(List<string> words)
    {
        var set = new HashSet<string>();
        for (var i = 0; i + ShingleSize <= words.Count; i++)
            set.Add(string.Join(' ', words.GetRange(i, ShingleSize)));
        return set;
    }

    private static string Normalize(string text) =>
        string.Join(' ', Tokenizer.Tokenize(text));
}
