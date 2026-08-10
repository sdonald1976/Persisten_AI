using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Pulls the subject a feeling is <em>about</em> out of a message — "stressed about <b>the
/// deadline</b>", "excited for <b>the trip</b>" — so a mood can be tied to a topic and followed up
/// on later ("how'd the interview go?"). Deterministic and conservative, in the same spirit as the
/// other detectors: it only returns a topic when the message says what the feeling is about, and it
/// rejects contentless objects like "it" / "that" / "everything" (you can't follow up on "it").
/// </summary>
public static partial class MoodTopic
{
    // "… about / over / regarding / with / because of <topic>"
    [GeneratedRegex(
        @"\b(?:about|over|regarding|concerning|because of|with)\s+(?<topic>[^.,;:!?\n]{2,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AboutPhrase();

    // "excited for / looking forward to <topic>" — anticipation attaches with for/to, not about.
    [GeneratedRegex(
        @"\b(?:excited|stoked|psyched|pumped|looking forward)\s+(?:for|to)\s+(?<topic>[^.,;:!?\n]{2,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForPhrase();

    // Where a topic phrase should be cut off — time/qualifier tails that aren't part of the subject.
    private static readonly string[] Tails =
    {
        " on ", " at ", " in ", " this ", " next ", " last ", " tomorrow", " today", " yesterday",
        " right now", " lately", " these days", " anymore", " again", " so much", " so bad",
    };

    // Determiners are kept in the phrase (they read naturally) but ignored when checking for content.
    private static readonly HashSet<string> Determiners = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "my", "your", "our", "his", "her", "their", "this", "that", "some", "any",
    };

    // Objects with no followable subject — a mood tied to one of these is treated as untied.
    private static readonly HashSet<string> Contentless = new(StringComparer.Ordinal)
    {
        "it", "that", "this", "them", "those", "these", "things", "thing", "stuff", "everything",
        "anything", "something", "nothing", "all", "him", "her", "me", "you", "us", "myself", "life",
    };

    [GeneratedRegex(@"[a-z']+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static string? Extract(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = AboutPhrase().Match(message);
        if (!match.Success)
            match = ForPhrase().Match(message);
        if (!match.Success)
            return null;

        var topic = match.Groups["topic"].Value.Trim().ToLowerInvariant();

        // Cut off a trailing time/qualifier tail ("my interview on thursday" → "my interview").
        foreach (var tail in Tails)
        {
            var idx = topic.IndexOf(tail, StringComparison.Ordinal);
            if (idx > 0)
                topic = topic[..idx];
        }

        topic = topic.Trim().TrimEnd(',', ';', ':', '-', ' ');
        if (topic.Length < 2)
            return null;

        // Require at least one real content word (not just a determiner or a contentless pronoun).
        var contentWords = Words().Matches(topic)
            .Select(m => m.Value)
            .Where(w => !Determiners.Contains(w) && !Contentless.Contains(w))
            .ToList();
        if (contentWords.Count == 0)
            return null;

        return topic;
    }
}
