using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Decides whether a user message is in-character play rather than biography, so the memory
/// system can enjoy the conversation without believing it. Two signals:
/// roleplay markup (<c>*hugs*</c>-style action asterisks), and any use of a relationship word
/// the persona has claimed ("sis", "my sister") — if the persona IS the user's sister, those
/// words are about <em>her</em>, and when they're genuinely ambiguous the guard prefers
/// protecting the fact store over capturing a maybe-fact. An in-character turn still gets a full
/// reply with full context; it just leaves no durable derived memory (the privacy-gate rule,
/// applied to fiction).
/// </summary>
public static partial class InCharacterDetector
{
    // *hugs you*, *smiles softly* — classic roleplay action markup.
    [GeneratedRegex(@"\*[^*\n]{1,80}\*", RegexOptions.CultureInvariant)]
    private static partial Regex ActionMarkup();

    public static bool IsInCharacter(string? message, PersonaLexicon lexicon)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (ActionMarkup().IsMatch(message))
            return true;

        return lexicon.MentionsRelationship(message);
    }
}
