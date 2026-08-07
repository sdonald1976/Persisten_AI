namespace Companion.Core.Domain;

/// <summary>
/// Who the companion is — name, gender, pronouns — resolved from the user's overrides falling back
/// to the configured defaults. Rendered into the system prompt so the identity holds across every
/// personality preset (and, later, the voice).
/// </summary>
public sealed record CompanionIdentity
{
    public required string Name { get; init; }
    public required string Gender { get; init; }
    public required string Pronouns { get; init; }

    /// <summary>The identity as a short instruction for the system prompt (empty if nothing is set).</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Name))
            parts.Add($"Your name is {Name.Trim()}.");

        var genderPhrase = GenderPhrase(Gender);
        if (genderPhrase is not null)
        {
            var pronouns = string.IsNullOrWhiteSpace(Pronouns) ? null : Pronouns.Trim();
            parts.Add(pronouns is null
                ? $"You are {genderPhrase}."
                : $"You are {genderPhrase}; use {pronouns} pronouns when referring to yourself.");
        }
        else if (!string.IsNullOrWhiteSpace(Pronouns))
        {
            parts.Add($"Use {Pronouns.Trim()} pronouns when referring to yourself.");
        }

        return string.Join(" ", parts);
    }

    private static string? GenderPhrase(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "female" or "woman" or "girl" => "a woman",
        "male" or "man" or "boy" => "a man",
        "nonbinary" or "non-binary" or "enby" => "nonbinary",
        var other => other,
    };
}
