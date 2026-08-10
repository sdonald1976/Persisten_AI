using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Spots a first-person <em>commitment</em> the companion makes in its own reply — "I'll check in
/// tomorrow", "I'm going to look that up for you" — so it can follow up on it later instead of
/// forgetting it said so. Deliberately conservative: it only fires on clear "I'll / I will / I'm
/// going to" promises and filters out conversational filler ("I'll be honest", "I'll admit"), so it
/// misses some rather than storing junk. Returns the promise as a predicate ("check in tomorrow")
/// that reads naturally after "I said I'd …", or null when there's no commitment.
/// </summary>
public static partial class CommitmentDetector
{
    [GeneratedRegex(
        @"\b(?:i'?ll|i will|i'?m going to|i am going to)\s+(?<what>[^.!?\n]{3,150})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Promise();

    // Openers that are conversational filler, not a real follow-up worth resurfacing.
    private static readonly string[] Filler =
    {
        "be honest", "be direct", "be brief", "admit", "say", "tell you", "explain",
        "note that", "point out", "add that", "assume", "guess", "bet", "try to",
        "do my best", "keep that in mind", "let you finish", "stop", "pause", "wait",
    };

    public static string? Detect(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        foreach (Match m in Promise().Matches(reply))
        {
            var what = m.Groups["what"].Value.Trim().TrimEnd(',', ';', ':');
            if (what.Length < 3)
                continue;

            var lead = what.ToLowerInvariant();
            if (Filler.Any(f => lead.StartsWith(f, StringComparison.Ordinal)))
                continue;

            return what;
        }

        return null;
    }
}
