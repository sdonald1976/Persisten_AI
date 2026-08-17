using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Spots work the user has said is <em>not done yet</em> — "I need to finish the export job",
/// "I've got to get the beds built this weekend" — so that something still opens a loop when the
/// extraction model doesn't.
///
/// It exists because relying on the model alone was measured and found to fail completely. Across
/// seventeen turns of ordinary conversation the extractor produced no episodic memories at all, and
/// open loops are only created from episodic candidates, so "what's unfinished?" answered "nothing"
/// while the user had said they were rebuilding an irrigation system before the frost with only the
/// weekend to do it. The prompt's own rule of thumb caused it — an unfinished job will still be
/// true next week, so it reads as a lasting fact — and the prompt is now explicit about that. This
/// is the backstop for when it isn't followed, which is a thing small local models do.
///
/// Conservative in the same way as <see cref="CommitmentDetector"/> and
/// <see cref="DecisionDetector"/>: it fires on clear obligation phrasing in the first person,
/// present or future, and would rather miss one than open a loop about nothing.
/// </summary>
public static partial class UnfinishedWorkDetector
{
    [GeneratedRegex(
        @"\b(?:i)\s*(?:'ve|\s+have)?\s*(?:still\s+)?(?:" +
        @"need\s+to|have\s+to|got\s+to|gotta|must|" +
        @"want\s+to\s+(?:get|finish|sort|build|write|fix)|" +
        @"(?:'m|\s+am)\s+(?:supposed\s+to|meant\s+to|in\s+the\s+middle\s+of)" +
        @")\s+(?<what>[^.!?\n]{3,200})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Obligation();

    /// <summary>
    /// Past-tense or already-settled phrasing that the obligation pattern can still match inside a
    /// longer sentence ("I needed to, and I did"). A finished thing is not an open loop.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:did|done|finished|sorted|already|no longer|don'?t need|didn'?t need)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Settled();

    /// <summary>Tails that are turn of phrase rather than a job of work.</summary>
    private static readonly string[] Filler =
    {
        "say", "admit", "be honest", "ask", "tell you", "know", "think", "sleep", "go",
        "run", "stop", "get going", "head off", "crack on", "disagree", "point out",
    };

    /// <summary>
    /// The outstanding task in the user's own words ("finish the export job tonight"), or null.
    /// Reads naturally after "you said you needed to …".
    /// </summary>
    public static string? Detect(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        foreach (Match m in Obligation().Matches(message))
        {
            var what = m.Groups["what"].Value.Trim().TrimEnd(',', ';', ':');
            if (what.Length < 3)
                continue;

            var lead = what.ToLowerInvariant();
            if (Filler.Any(f => lead == f || lead.StartsWith(f + " ", StringComparison.Ordinal)))
                continue;
            if (Settled().IsMatch(what))
                continue;

            return what;
        }

        return null;
    }
}
