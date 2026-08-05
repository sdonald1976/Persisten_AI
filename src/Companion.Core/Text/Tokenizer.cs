using System.Text;

namespace Companion.Core.Text;

/// <summary>
/// Minimal, deterministic tokenizer shared by keyword matching and the mock embedding so
/// the two stay consistent. Lowercases, splits on non-alphanumerics, drops stopwords, and
/// applies a tiny suffix stemmer so "tested"/"testing"/"tests" collapse to "test".
/// This is intentionally simple; a real deployment would use the model's own tokenizer.
/// </summary>
public static class Tokenizer
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "so", "of", "to", "in", "on",
        "at", "for", "with", "is", "am", "are", "was", "were", "be", "been", "being",
        "it", "its", "this", "that", "these", "those", "i", "you", "he", "she", "we",
        "they", "me", "my", "your", "our", "their", "do", "did", "does", "have", "has",
        "had", "will", "would", "can", "could", "should", "there", "here", "as", "by",
        "from", "about", "into", "out", "up", "down", "not", "no", "yet", "just",
    };

    /// <summary>Tokenizes text into normalized, stemmed content words.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var sb = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                Emit(sb.ToString(), tokens);
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            Emit(sb.ToString(), tokens);

        return tokens;
    }

    private static void Emit(string raw, List<string> tokens)
    {
        if (raw.Length < 2 || Stopwords.Contains(raw))
            return;
        tokens.Add(Stem(raw));
    }

    /// <summary>Very small suffix stemmer — enough to align obvious inflections.</summary>
    private static string Stem(string word)
    {
        if (word.Length > 4 && word.EndsWith("ing", StringComparison.Ordinal))
            return word[..^3];
        if (word.Length > 3 && word.EndsWith("ed", StringComparison.Ordinal))
            return word[..^2];
        if (word.Length > 3 && word.EndsWith("es", StringComparison.Ordinal))
            return word[..^2];
        if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }
}
