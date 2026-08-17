using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Refuses a fact that is plainly about somebody else, however the extractor labelled it.
///
/// A companion is told about other people constantly — a daughter's obsession, a partner's job, a
/// colleague's move — and every one of those was landing in the user's own profile. Across forty
/// generated lives, "my daughter Immy is really into rockpooling" became the USER'S interest in
/// forty of forty.
///
/// The schema now asks whose fact it is, and the model answers "user" anyway about two times in
/// three, while writing content that says otherwise: <c>subject=user</c> beside "The user's son Rafe
/// is currently interested in climbing." So the label is not load-bearing and the sentence is.
///
/// This is a veto and only a veto, in the same shape as <see cref="AssertionGuard"/>: it can refuse
/// a candidate the extractor proposed and can never invent one. It keys on a possessive naming
/// another person — "the user's daughter", "their friend Nell" — which is a structural property of
/// the sentence rather than a topic list, and it deliberately errs towards admitting: a fact wrongly
/// kept is untidy, a fact wrongly refused is one the user told us and we discarded.
/// </summary>
public static partial class SubjectGuard
{
    /// <summary>
    /// Kinship and relationship words. Short and dull on purpose — these are the nouns that follow a
    /// possessive when somebody is talking about a person rather than a thing, and the list only has
    /// to separate "the user's daughter" from "the user's coffee".
    /// </summary>
    private const string People =
        "son|daughter|wife|husband|partner|girlfriend|boyfriend|mother|mum|father|dad|parents|" +
        "brother|sister|sibling|child|children|kid|kids|friend|colleague|boss|neighbour|neighbor|" +
        "grandmother|grandfather|gran|grandad|nephew|niece|cousin|aunt|uncle|flatmate|housemate";

    /// <summary>
    /// A possessive introducing another person, at the head of the statement: "The user's daughter
    /// Immy is…", "Their friend Nell has…". Anchored to the start because that is where the
    /// sentence's subject lives — "the user bought a present for their daughter" is still a fact
    /// about the user and must survive.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(?:the\s+)?(?:user'?s?|their|his|her)\s+(?:" + People + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PossessivePerson();

    /// <summary>
    /// The extractor naming a person outright in the subject field — "Immy", "Rafe", "my daughter".
    /// Anything that is not the user, and not empty, is somebody else.
    /// </summary>
    public static bool IsAboutSomeoneElse(string? subject, string? content)
    {
        if (!string.IsNullOrWhiteSpace(subject)
            && !subject.Trim().Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(content) && PossessivePerson().IsMatch(content);
    }

    /// <summary>Who it turned out to be about, for the audit trail.</summary>
    public static string Explain(string? subject, string? content)
    {
        if (!string.IsNullOrWhiteSpace(subject)
            && !subject.Trim().Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return $"this is about {subject.Trim()}, not the user";
        }

        var match = PossessivePerson().Match(content ?? "");
        return match.Success
            ? $"this is about {match.Value.Trim()}, not the user"
            : "this is about somebody other than the user";
    }
}
