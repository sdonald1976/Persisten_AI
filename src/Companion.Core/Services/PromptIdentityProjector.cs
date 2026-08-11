using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Builds the model-facing participant identity from existing profile and companion identity
/// fields. Stored persona text remains untouched; only ambiguous relationship wording is
/// projected into role-aware facts for the prompt.
/// </summary>
public static class PromptIdentityProjector
{
    private static readonly Regex YouAreMyRelation = new(
        @"\byou\s+are\s+my\s+(sister|brother|sibling)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IAmYourRelation = new(
        @"\bi\s+am\s+your\s+(sister|brother|sibling)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PromptIdentityContext From(UserProfile profile, CompanionIdentity companion)
    {
        var context = new PromptIdentityContext
        {
            CompanionName = Clean(companion.Name),
            CompanionGender = Clean(companion.Gender),
            CompanionPronouns = Clean(companion.Pronouns),
            UserName = Clean(profile.DisplayName),
        };

        var lines = RelationshipLines(profile.Persona, context.CompanionRef, context.UserRef);
        return context with { RelationshipLines = lines };
    }

    private static IReadOnlyList<string> RelationshipLines(string? persona, string companion, string user)
    {
        if (string.IsNullOrWhiteSpace(persona))
            return Array.Empty<string>();

        var lines = new List<string>();
        foreach (Match match in YouAreMyRelation.Matches(persona))
            AddReciprocal(lines, companion, user, match.Groups[1].Value);
        foreach (Match match in IAmYourRelation.Matches(persona))
            AddReciprocal(lines, companion, user, match.Groups[1].Value);

        return lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddReciprocal(List<string> lines, string companion, string user, string relation)
    {
        var normalized = relation.Trim().ToLowerInvariant();
        lines.Add($"{companion} is {Possessive(user)} {normalized}.");

        var reciprocal = normalized switch
        {
            "sister" => "brother",
            "brother" => "sister",
            _ => "sibling",
        };
        lines.Add($"{user} is {Possessive(companion)} {reciprocal}.");
    }

    private static string Possessive(string name)
        => name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? name + "'" : name + "'s";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
