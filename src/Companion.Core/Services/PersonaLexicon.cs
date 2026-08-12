using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// What the companion's persona claims to BE to the user — extracted from the composed persona
/// text so the memory system can tell in-character talk from biography. If the persona casts her
/// as the user's mentor, then relationship references in conversation may mean <em>her</em>,
/// and treating it as a fact about the user's real family is exactly how personas leak into the
/// fact store. The lexicon is rebuilt from the live persona each time, so editing the persona
/// immediately changes what counts as in-character.
/// </summary>
public sealed record PersonaLexicon
{
    /// <summary>The companion's name, when known (used to filter facts about her, not to flag turns).</summary>
    public string? CompanionName { get; init; }

    /// <summary>Relationship words the persona claims, including common shorthand.</summary>
    public IReadOnlyList<string> RelationshipNouns { get; init; } = Array.Empty<string>();

    public bool HasRelationshipNouns => RelationshipNouns.Count > 0;

    /// <summary>Relationship words a persona can claim; each canonical form brings its variants.</summary>
    private static readonly string[][] Vocabulary =
    {
        new[] { "mentor", "coach", "teacher" },
        new[] { "partner" },
        new[] { "girlfriend", "gf" },
        new[] { "boyfriend", "bf" },
        new[] { "wife" },
        new[] { "husband" },
        new[] { "spouse" },
        new[] { "partner" },
        new[] { "lover" },
        new[] { "fiancee", "fiance", "fiancée", "fiancé" },
        new[] { "mother", "mom", "mommy", "mum" },
        new[] { "father", "dad", "daddy" },
        new[] { "aunt", "auntie" },
        new[] { "uncle" },
        new[] { "cousin" },
    };

    /// <summary>Builds the lexicon by scanning the composed persona text for relationship claims.</summary>
    public static PersonaLexicon From(string? companionName, string? personaText)
    {
        var nouns = new List<string>();
        if (!string.IsNullOrWhiteSpace(personaText))
        {
            foreach (var family in Vocabulary)
            {
                if (family.Any(word => ContainsWord(personaText, word)))
                    nouns.AddRange(family);
            }
        }

        return new PersonaLexicon
        {
            CompanionName = string.IsNullOrWhiteSpace(companionName) ? null : companionName.Trim(),
            RelationshipNouns = nouns,
        };
    }

    /// <summary>True when the text uses any relationship word the persona has claimed.</summary>
    public bool MentionsRelationship(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && RelationshipNouns.Any(noun => ContainsWord(text!, noun));

    /// <summary>True when the text references the companion at all (name or claimed relationship).</summary>
    public bool MentionsCompanion(string? text)
        => MentionsRelationship(text)
            || (CompanionName is not null && !string.IsNullOrWhiteSpace(text) && ContainsWord(text!, CompanionName));

    private static bool ContainsWord(string text, string word)
        => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
