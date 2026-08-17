using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Decides whether a cited excerpt is something the user actually <em>asserted</em>, as opposed to
/// something they merely said the words of.
///
/// Evidence verification proves an excerpt appears in a real user message. That is a check against
/// invention, not against misreading: "Did I ever tell you what timber I bought for the beds?"
/// contains the words "timber I bought for the beds", so an extractor can cite it perfectly
/// honestly and still turn a question into the durable fact "The user bought timber". That happened
/// in a real conversation — she said "I don't recall you mentioning that" and then stored it as a
/// fact in the same turn.
///
/// So the excerpt is located in its own sentence and that sentence has to be a statement. A
/// question presupposes; it does not assert. The same reasoning covers the other non-assertions
/// that carry a proposition in plain words: hypotheticals ("if I bought cedar…"), and things
/// attributed to someone else asking ("she asked whether I'd bought the timber").
/// </summary>
public static partial class AssertionGuard
{
    /// <summary>Sentence terminators, kept with the sentence they end.</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Openers that make a clause a question even without a question mark — people drop them in
    /// chat ("did I tell you about the timber", "wondering if you remember the timber").
    /// </summary>
    [GeneratedRegex(
        @"^(?:so\s+|and\s+|but\s+|also\s+|ok(?:ay)?,?\s+|hey,?\s+)*" +
        @"(?:who|what|when|where|why|how|which|whose|whom" +
        @"|do|does|did|is|are|was|were|am|can|could|will|would|shall|should|have|has|had|may|might" +
        @"|wondering\s+(?:if|whether)|any\s+idea|remind\s+me)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Interrogative();

    /// <summary>Conditionals and suppositions — stated, but not stated as true.</summary>
    [GeneratedRegex(
        @"^(?:so\s+|and\s+|but\s+|also\s+)*(?:if|suppose|supposing|imagine|say)\b" +
        @"|^what\s+if\b|\bhypothetically\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Supposition();

    /// <summary>
    /// True when <paramref name="excerpt"/> appears in <paramref name="message"/> inside at least
    /// one sentence the user is asserting. Unknown or unlocatable text is treated as asserted —
    /// this guard exists to catch a specific, demonstrable error, not to become a second opinion on
    /// everything the extractor proposes.
    /// </summary>
    public static bool IsAsserted(string? excerpt, string? message)
    {
        if (string.IsNullOrWhiteSpace(excerpt) || string.IsNullOrWhiteSpace(message))
            return true;

        var sentences = Sentences(message);
        if (sentences.Count == 0)
            return true;

        var matching = sentences.Where(s => Overlaps(excerpt, s)).ToList();

        // The excerpt is a paraphrase we cannot place in any one sentence. Fall back to the whole
        // message: reject only when every sentence in it is a non-assertion, so a fact stated
        // alongside a question still survives.
        if (matching.Count == 0)
            matching = sentences;

        return matching.Any(s => IsStatement(s) || AssertedInALeadingClause(excerpt, s));
    }

    /// <summary>
    /// A claim made on the way to asking something else: "Since I've moved to Cambridge, do you know
    /// any good cafés?" is a question, but "I've moved to Cambridge" is still asserted in it. Only
    /// clauses BEFORE the final one count — the last clause is the one carrying the question mark.
    /// </summary>
    private static bool AssertedInALeadingClause(string excerpt, string sentence)
    {
        var clauses = sentence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (clauses.Length < 2)
            return false;

        return clauses
            .Take(clauses.Length - 1)
            .Any(clause => Overlaps(excerpt, clause) && IsStatement(clause));
    }

    /// <summary>The reason an excerpt was refused, for the audit trail.</summary>
    public static string Explain(string? excerpt, string? message)
    {
        var sentence = Sentences(message ?? "").FirstOrDefault(s => Overlaps(excerpt ?? "", s));
        return sentence is null
            ? "the user did not state this — every sentence it could come from is a question or a supposition"
            : $"the user asked this rather than stating it: \"{Trim(sentence)}\"";
    }

    private static bool IsStatement(string sentence)
    {
        var trimmed = sentence.Trim();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.EndsWith('?'))
            return false;
        if (Interrogative().IsMatch(trimmed))
            return false;
        if (Supposition().IsMatch(trimmed))
            return false;
        return true;
    }

    private static List<string> Sentences(string text)
        => SentenceBreak().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>
    /// Whether the excerpt and the sentence are talking about the same words. An exact substring
    /// settles it; otherwise most of the excerpt's words have to be in the sentence, because models
    /// paraphrase what they cite and `ResolveEvidence` already lets them.
    /// </summary>
    private static bool Overlaps(string excerpt, string sentence)
    {
        var e = Normalize(excerpt);
        var s = Normalize(sentence);
        if (e.Length == 0 || s.Length == 0)
            return false;
        if (s.Contains(e, StringComparison.Ordinal) || e.Contains(s, StringComparison.Ordinal))
            return true;

        var excerptWords = Words(e);
        if (excerptWords.Count == 0)
            return false;
        var sentenceWords = Words(s).ToHashSet();
        return excerptWords.Count(sentenceWords.Contains) / (double)excerptWords.Count >= 0.6;
    }

    private static string Normalize(string text)
        => Whitespace().Replace(text, " ").Trim().ToLowerInvariant();

    private static List<string> Words(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')'))
            .Where(w => w.Length > 0)
            .ToList();

    private static string Trim(string text)
        => text.Length <= 80 ? text : text[..80] + "…";
}
