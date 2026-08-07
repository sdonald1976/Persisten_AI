using System.Text.RegularExpressions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Deterministic, topic-free signals for the "should we keep going?" decision. Two independent
/// questions, neither of which needs a model call or a list of topics:
///
///   • <see cref="IsDeliverableRequest"/> — did the user ask for a *produced artifact* (write, draft,
///     list, explain in detail, …) rather than make conversation? A property of the request, not the
///     subject. Only deliverables are ever chased to completion, so ordinary chat is never continued.
///   • <see cref="LooksUnfinished"/> — does the reply itself show a structural sign of being cut off
///     (mid-sentence, an open code fence, a trailing "want me to continue?", a dangling colon)?
///
/// The generator continues only when a deliverable request produced an unfinished-looking reply —
/// so completion detection is cheap and explainable, and the unreliable semantic judge is a last
/// resort rather than the primary signal.
/// </summary>
internal static partial class CompletionSignals
{
    [GeneratedRegex(
        @"\b(write|writes|writing|wrote|draft|drafts|drafting|compose|composing|generate|generating|" +
        @"create|creating|build|building|list|lists|outline|outlines|enumerate|summariz|summaris|" +
        @"rewrite|revise|expand|elaborate|translate|translating|implement|plan|brainstorm|describe|" +
        @"explain|explaining|continue|keep going|walk me through)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeliverableVerbs();

    // Phrase cues that imply a long, complete output is expected.
    private static readonly string[] DeliverablePhrases =
    {
        "in detail", "step by step", "step-by-step", "a list of", "give me a list",
        "as many as", "at least", "in full", "the whole", "each one",
    };

    [GeneratedRegex(
        @"((would you like me to|want me to|shall i|should i|do you want me to)\s+(continue|go on|keep going))" +
        @"|to be continued|\b(continue|keep going|more)\?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationSolicitation();

    // Characters that end a finished thought. Anything else at the end reads as mid-sentence.
    private const string TerminalPunctuation = ".!?…\"')]}”’`";

    public static bool IsDeliverableRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (DeliverableVerbs().IsMatch(message))
            return true;

        foreach (var phrase in DeliverablePhrases)
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    public static bool LooksUnfinished(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        var trimmed = reply.TrimEnd();

        // An unclosed ``` code fence — half a code block is never finished.
        if (CountOccurrences(trimmed, "```") % 2 == 1)
            return true;

        // An explicit offer/promise to continue.
        if (ContinuationSolicitation().IsMatch(trimmed))
            return true;

        var last = trimmed[^1];

        // A dangling colon promises a list/section that never came.
        if (last == ':')
            return true;

        // Ends mid-sentence — no terminal punctuation.
        return !TerminalPunctuation.Contains(last);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
