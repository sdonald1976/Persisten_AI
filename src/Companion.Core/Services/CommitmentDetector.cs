using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Spots a first-person <em>commitment</em> the companion makes in its own reply — "I'll check in
/// tomorrow", "I'm going to look that up for you" — so it can follow up on it later instead of
/// forgetting it said so. Returns the promise as a predicate ("check in tomorrow") that reads
/// naturally after "I said I'd …", or null when there's no commitment.
///
/// What it accepts is a short allow-list of verbs for things she can actually do, rather than a
/// block-list of filler. The block-list let a hallucination become permanent: asked how the user's
/// allotment was coming along, the model answered as though the plot were hers — "I'll have some
/// space set aside for experimental varieties, maybe some heirloom tomatoes" — and that sentence,
/// being a well-formed first-person promise, was stored as an open loop and opened the next
/// session with "Last time I said I'd have some space set aside…". She has no allotment. Fabricated
/// text is not distinguishable from real text by looking at its grammar, so the filter has to be
/// about capability instead: she can look something up, draft something, remind someone. She cannot
/// set aside space in a garden, and a promise to do something she cannot do is a promise from a
/// story rather than from her.
/// </summary>
public static partial class CommitmentDetector
{
    [GeneratedRegex(
        @"\b(?:i'?ll|i will|i'?m going to|i am going to)\s+(?<what>[^.!?\n]{3,150})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Promise();

    /// <summary>
    /// Verbs naming something she can genuinely carry out: they concern this conversation, the
    /// things she remembers, or the next time they speak. Anything else — building, planting,
    /// buying, going somewhere — belongs to a life she does not have.
    /// </summary>
    [GeneratedRegex(
        @"^(?:go\s+(?:and\s+)?)?(?:" +
        @"check(?:\s+in|\s+back|\s+on)?|follow\s+up|ask|remind|nudge|chase|" +
        @"look\b|read|research|find\s+out|dig\s+into|" +
        @"draft|write|note|jot|make\s+a\s+note|keep\s+(?:a\s+)?(?:note|track)|record|remember|" +
        @"put\s+together|pull\s+together|work\s+(?:out|through)|think\s+(?:about|through)|" +
        @"send|share|email|message|list|summari[sz]e|explain|walk\s+you\s+through|help\s+you|" +
        @"let\s+you\s+know|get\s+back\s+to\s+you|bring\s+(?:it|that|this)\s+up|flag|save|add|" +
        @"keep\s+an\s+eye|" +
        @"stop|hold\s+off|leave\s+(?:it|that)|drop\s+(?:it|that)" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Actionable();

    public static string? Detect(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        foreach (Match m in Promise().Matches(reply))
        {
            var what = m.Groups["what"].Value.Trim().TrimEnd(',', ';', ':');
            if (what.Length < 3)
                continue;

            if (!Actionable().IsMatch(what))
                continue;

            return what;
        }

        return null;
    }
}
