using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Rejects candidate memories that treat an unresolved pronoun as if it were a person. The
/// live specimen this guard exists for: the user said "I'm planning a small dinner for her",
/// working context knew who "her" was, extraction did not — and the store gained
/// "The user is planning a small dinner for someone named her." A fact whose person is a
/// pronoun is unknowable, not misspellable: there is nothing true to normalize it into, so it
/// is refused, and the right fix is upstream (pass the resolution to extraction), not here.
/// Deliberately narrow — "named her dog Precious" is a real sentence and must not trip it.
/// </summary>
public static partial class UnresolvedReferentGuard
{
    public const string Explanation =
        "treats an unresolved pronoun as a person (\"someone named her\") — unknowable, not stored";

    public static bool IsPronounAsPerson(MemoryCandidate candidate)
        => Trips(candidate.Content) || Trips(candidate.Value);

    private static bool Trips(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();

        // A value that IS a bare pronoun ("her", "him", "them") names nobody.
        if (BarePronoun().IsMatch(trimmed))
            return true;

        // "someone named her" / "a person called him" — a pronoun in the naming position.
        // "named her dog Precious" survives: the pronoun there is followed by the real object.
        if (PronounAsName().IsMatch(trimmed))
            return true;

        // A DANGLING object pronoun: "knitting a scarf for her." — the person is outside the
        // sentence, so as a durable fact read weeks later the referent is gone. Possessive use
        // survives ("walks her dog": the pronoun is followed by its noun), and so does any
        // fact that states a real name alongside. This shipped after the first live run of
        // the resolution boundary: the naming patterns above were dodged by an extractor that
        // simply kept the pronoun, which is quieter garbage, not less garbage.
        return DanglingObjectPronoun().IsMatch(trimmed);
    }

    /// <summary>
    /// On a turn whose reference stayed ambiguous: the first capitalized name in the candidate
    /// that appears in none of the user's own words this turn — i.e. a person the model
    /// supplied. Null when every name is the user's. Calendar words and sentence-case openers
    /// are not names.
    /// </summary>
    public static string? NamesSomeoneTheUserDidNot(
        MemoryCandidate candidate, IReadOnlyList<string> userSaid)
    {
        var text = $"{candidate.Content} {candidate.Value}";
        foreach (Match m in NameCandidate().Matches(text))
        {
            var name = m.Value;
            if (CommonCapitalized().IsMatch(name))
                continue;
            // Sentence-case openers ("The user…") never reach here thanks to the common-word
            // list; anything else must be traceable to the user's own words this turn.
            if (!userSaid.Any(said => said.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return null;
    }

    [GeneratedRegex(@"\b[A-Z][a-z]+\b")]
    private static partial Regex NameCandidate();

    /// <summary>Capitalized words that are structure, not people: sentence heads the
    /// normalizer produces, calendar words, and the fact-template vocabulary.</summary>
    [GeneratedRegex(@"^(The|A|An|User|She|He|They|It|Their|Her|His|Its|And|Or|But|If|When|While|" +
        @"Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|" +
        @"January|February|March|April|May|June|July|August|September|October|November|December|" +
        @"Today|Tomorrow|Yesterday|Spring|Summer|Autumn|Fall|Winter)$")]
    private static partial Regex CommonCapitalized();

    [GeneratedRegex(@"^(her|him|them)$", RegexOptions.IgnoreCase)]
    private static partial Regex BarePronoun();

    [GeneratedRegex(@"\b(someone|somebody|a (?:person|woman|man|friend))\s+(named|called)\s+(her|him|them)\b(?!\s+\w)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PronounAsName();

    /// <summary>An object pronoun after a preposition with nothing following it — the referent
    /// lives outside the sentence. "for her." trips; "her dog" (possessive + noun) does not.</summary>
    [GeneratedRegex(@"\b(for|with|to|about|from|after)\s+(her|him|them)\b(?!\s+\w)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DanglingObjectPronoun();
}
