using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>A detected explicit teaching: the concept term and the definitional sentence.</summary>
/// <param name="Term">The taught concept, as written ("axe", "Disney World").</param>
/// <param name="Gloss">The complement — what it is defined as.</param>
/// <param name="Sentence">The full definitional sentence, verbatim — the evidence excerpt
/// and the retrievable text.</param>
public sealed record TeachingCandidate(string Term, string Gloss, string Sentence);

/// <summary>
/// Detects EXPLICIT definitional teaching, and nothing else. Deliberately high-precision,
/// low-recall: a missed teaching opportunity costs a captured corpus row; a false positive
/// permanently stores an accidental remark as Ava-owned world knowledge. "An axe is sitting
/// in my garage" contains "axe is" and must never teach.
///
/// The gates, all of which must pass:
///   subject   — a GENERIC noun phrase: "a/an/the + common noun(s)" or a capitalized proper
///               name; never possessive ("my axe"), demonstrative ("that axe"), or pronoun.
///   copula    — present-tense "is/are/means" only; past tense is narrative, not teaching.
///   gloss     — must be a category phrase: article-initial ("a tool …") for is/are, and
///               free-form for "means"; never verb-progressive ("sitting in…"), never
///               adverb-led ("probably…"), never bare adjectives ("expensive"), and never
///               containing first/second-person pronouns (those make it about someone's
///               life, which is biography's jurisdiction).
/// Misses are expected and measured: every loose-shaped sentence the gates reject is
/// captured under `knowledge.teaching`, and the detector only broadens on that corpus,
/// never on intuition (the ToolNudge lesson).
/// </summary>
public static partial class TeachingDetector
{
    private const int MaxTermWords = 3;
    private const int MinGlossChars = 15;
    private const int MaxSentenceChars = 400;

    /// <summary>The single definitional teaching in the message, or null. Only the first
    /// qualifying sentence is taken — one teaching per turn keeps the store deliberate.</summary>
    public static TeachingCandidate? Detect(string message)
    {
        foreach (var raw in Sentences(message))
        {
            var sentence = raw.Trim();
            if (sentence.Length is 0 or > MaxSentenceChars)
                continue;

            var m = Copular().Match(sentence);
            if (!m.Success)
                continue;

            var term = m.Groups["term"].Value.Trim();
            var copula = m.Groups["copula"].Value.ToLowerInvariant();
            var gloss = m.Groups["gloss"].Value.Trim().TrimEnd('.', '!');

            if (!SubjectIsGeneric(m.Groups["det"].Value, term))
                continue;
            if (gloss.Length < MinGlossChars)
                continue;
            if (PersonalPronoun().IsMatch(gloss))
                continue;
            // A definition is timeless; a gloss anchored to a moment is a remark about now.
            if (TemporalWord().IsMatch(gloss))
                continue;
            if (copula is "is" or "are")
            {
                // The gloss must be a category phrase. "a tool used for chopping" passes;
                // "sitting in my garage", "probably what I need", and "expensive" do not.
                if (!ArticleInitial().IsMatch(gloss))
                    continue;
                var afterArticle = ArticleInitial().Replace(gloss, "");
                if (Progressive().IsMatch(afterArticle))
                    continue;
            }

            return new TeachingCandidate(term, gloss, sentence);
        }
        return null;
    }

    /// <summary>The loose copular shape — the CAPTURE population. Everything matching this
    /// that <see cref="Detect"/> rejects is a labeled negative for the future corpus.</summary>
    public static bool LooseShape(string message)
        => Sentences(message).Any(s => Copular().IsMatch(s.Trim()));

    private static bool SubjectIsGeneric(string determiner, string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > MaxTermWords)
            return false;
        if (words.Any(w => Blocked().IsMatch(w)))
            return false;

        // With an article the subject is generic by construction ("an axe", "the tide").
        if (determiner.Length > 0)
            return true;

        // Without one, only a proper name qualifies ("Disney World is a theme park…") —
        // every word capitalized. Bare common nouns ("friendship is…") are a recorded miss.
        return words.All(w => char.IsUpper(w[0]));
    }

    private static IEnumerable<string> Sentences(string text)
        => text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s + ".");

    /// <summary>"An axe is …", "The tide means …", "Disney World is …". The determiner and
    /// term are captured separately so the subject gate can reason about them.</summary>
    [GeneratedRegex(
        @"^(?:(?<det>a|an|the)\s+)?(?<term>[A-Za-z][A-Za-z'-]*(?:\s+[A-Za-z][A-Za-z'-]*){0,2}?)\s+(?<copula>is|are|means)\s+(?<gloss>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Copular();

    [GeneratedRegex(@"\b(today|tonight|tomorrow|yesterday|now|currently|right now|this (week|month|year|morning|evening|afternoon)|at the moment|lately)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TemporalWord();

    [GeneratedRegex(@"^(a|an|the)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleInitial();

    [GeneratedRegex(@"^\w+ing\b", RegexOptions.IgnoreCase)]
    private static partial Regex Progressive();

    [GeneratedRegex(@"\b(I|I'm|I've|me|my|mine|you|you're|your|yours|we|we're|our|ours|us)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PersonalPronoun();

    /// <summary>Words that disqualify a subject: possessives, demonstratives, pronouns,
    /// quantifier noise.</summary>
    [GeneratedRegex(@"^(my|your|his|her|its|our|their|this|that|these|those|it|he|she|they|there|some|any|every|each|no)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Blocked();
}
