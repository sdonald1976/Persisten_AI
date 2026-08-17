using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Spots an explicit <em>decision</em> in the user's own words — "we decided to use SQLite",
/// "I'm going with the Jetson", "let's stick with ntfy" — so it can be recorded against the
/// turn's project and answered later ("didn't we decide…?"). Deliberately conservative, like
/// <see cref="CommitmentDetector"/>: it fires only on clear decision phrasing and filters
/// filler ("let's go with the flow"), preferring to miss some over storing junk.
/// </summary>
public static partial class DecisionDetector
{
    [GeneratedRegex(
        @"\b(?:" +
        @"(?:i|we)(?:'ve| have)?\s+(?:decided|settled)\s+(?:on|to|that)|" +
        @"(?:i'?m|we'?re|i am|we are)\s+going\s+(?:with|to go with)|" +
        @"let'?s\s+(?:go with|use|stick with)|" +
        @"(?:i|we)\s+(?:chose|picked|opted for|went with)" +
        @")\s+(?<what>[^.!?\n]{3,200})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Decided();

    // Tails that are idiom, not a decision worth recording.
    private static readonly string[] Filler =
    {
        "the flow", "it", "that", "this", "whatever", "your gut", "my gut", "the punches",
    };

    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (Match m in Decided().Matches(text))
        {
            var what = m.Groups["what"].Value.Trim().TrimEnd(',', ';', ':');
            if (what.Length < 3)
                continue;

            var lead = what.ToLowerInvariant();
            if (Filler.Any(f => lead == f || lead.StartsWith(f + " ", StringComparison.Ordinal)))
                continue;

            // A decision the user only asked about, or supposed, is not a decision. The phrasing
            // is identical — "we decided on Postgres" and "have we decided on Postgres yet?" share
            // every word the pattern looks for — and the difference is the mood of the sentence,
            // which is the same distinction that stops a question becoming a stored fact.
            // Both of these were live false positives, found by tools/Companion.Eval.
            if (!AssertionGuard.IsAsserted(m.Value, text))
                continue;

            return what;
        }

        return null;
    }
}
