using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Decides whether a new fact <em>replaces</em> an existing one or <em>joins</em> it.
///
/// The original rule was "same subject+predicate slot, and similar enough, means the value changed".
/// It failed in both directions in one real conversation. Starting a second project put both
/// projects in the slot <c>user/works_on</c>, so the first one was superseded and silently lost —
/// the audit trail read "User stated a new value for 'works_on'", which is exactly what it thought
/// it saw. Meanwhile a genuine change — black coffee to oat milk lattes — landed in the slots
/// <c>drinks_coffee_black</c> and <c>prefers</c>, because the extraction model invents the
/// predicate, so no contradiction was even considered and both facts stayed current.
///
/// Similarity cannot rescue that rule. Measured against this project's own embedding model
/// (nomic-embed-text), the pair that MUST replace scores below the pair that MUST coexist:
///
/// <code>
///   0.763  coffee black      -> oat milk lattes   (must replace)
///   0.753  dislikes coriander vs dislikes olives  (must coexist)
///   0.729  greenhouse irrigation vs raised beds   (must coexist)
/// </code>
///
/// There is no threshold in that ordering. So similarity is demoted to a floor — it only stops a
/// replacement landing on something unrelated — and the decision is made by two things that are
/// actually about the fact rather than about its wording:
///
/// <list type="number">
/// <item>whether the predicate can hold more than one value at once — a person has one name and
/// one birthday, but any number of projects, pets and dislikes; and</item>
/// <item>whether the user's own words say they are replacing something ("actually", "I've gone off",
/// "no longer", "switched to"). A person adding a fact and a person changing one do not talk the
/// same way, and that difference is in the message even when it is invisible in the embedding.</item>
/// </list>
/// </summary>
public static partial class FactSupersession
{
    /// <summary>
    /// Predicates that hold exactly one value at a time, so a new value necessarily displaces the
    /// old one. Everything absent from this list is treated as multi-valued, because that is the
    /// safe default: wrongly keeping two facts is a tidiness problem, wrongly dropping one destroys
    /// something the user told us and cannot be noticed from inside the system.
    /// </summary>
    private static readonly HashSet<string> SingleValued = new(StringComparer.Ordinal)
    {
        "name", "full_name", "first_name", "last_name", "nickname", "goes_by",
        "age", "birthday", "date_of_birth", "born", "born_in", "born_on",
        "lives_in", "lives_at", "location", "home", "hometown", "city", "country", "address",
        "timezone", "nationality",
        "job", "occupation", "profession", "works_as", "employer", "job_title",
        "gender", "pronouns",
        "email", "email_address", "phone", "phone_number",
        "marital_status", "relationship_status",
        "height", "weight",
        // A current diet is one state at a time in the way a job is, not a collection like
        // "dislikes": someone who takes up keto has stopped being low-carb.
        "diet",
    };

    /// <summary>
    /// Phrases in which a person marks that something has changed rather than been added. Each is
    /// anchored to a first-person subject where the phrase alone would be too common — "instead"
    /// and "now" turn up in ordinary sentences constantly, and a false positive here deletes a
    /// memory.
    /// </summary>
    [GeneratedRegex(
        @"\bactually\b" +
        @"|\bno longer\b" +
        // "not any more", but also "don't do keto any more" — the negation and the "any more" are
        // routinely several words apart, and contracted.
        @"|(?:\bnot\b|n'?t\b|\bnever\b)[^.!?\n]{0,40}\bany\s?more\b" +
        @"|\bi(?:'ve| have)? ?(?:gone off|stopped|quit|given up|gave up)\b" +
        @"|\bi(?:'ve| have)? ?(?:switched|swapped|changed|moved) (?:to|over to|from)\b" +
        @"|\bi used to\b" +
        @"|\bchanged my mind\b|\bscratch that\b|\bcorrection\b|\bi meant\b" +
        @"|\bthese days\b|\bnowadays\b" +
        @"|\bit'?s .{0,40} now\b|\bi'?m .{0,40} now\b" +
        @"|\binstead of\b|\brather than\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplacementPhrase();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    /// <summary>
    /// True when this predicate can only hold one value, so a new value replaces the old without
    /// the user having to say so.
    /// </summary>
    public static bool IsSingleValued(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate))
            return false;
        return SingleValued.Contains(Canonical(predicate));
    }

    /// <summary>
    /// True when any of these fragments of the user's own words marks a change rather than an
    /// addition. Pass the candidate's cited excerpts and the user's messages from this turn: the
    /// excerpt is the more precise signal, the whole message the more forgiving one, and the
    /// similarity floor at the call site is what stops the looser read doing damage.
    /// </summary>
    public static bool SignalsReplacement(params IEnumerable<string>?[] sources)
        => sources.Any(source => source?.Any(
            text => !string.IsNullOrWhiteSpace(text) && ReplacementPhrase().IsMatch(text)) == true);

    /// <summary>Lowercased, punctuation-and-spacing-insensitive form: "Lives In" and "lives_in" agree.</summary>
    private static string Canonical(string predicate)
        => NonWord().Replace(predicate.Trim().ToLowerInvariant(), "_").Trim('_');
}
