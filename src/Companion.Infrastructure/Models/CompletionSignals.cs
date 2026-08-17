using System.Globalization;
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

    /// <summary>
    /// The user describing their <em>own</em> work — "I'm writing a talk", "we built the deck last
    /// year". The verb list alone cannot tell that from a request, because it looks only at the
    /// word: "I'm writing a talk on soil chemistry for the county show" was read as an instruction
    /// to write one, which put an ordinary conversational turn on the deliverable path and let the
    /// completion judge chase it through five rounds and four separate sign-offs.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:i|we)\s*(?:'m|'ve|'ll|\s+am|\s+have|\s+was|\s+were|\s+had|\s+will)?\s*" +
        @"(?:just\s+|been\s+|currently\s+|still\s+|finally\s+)*" +
        @"(?:writing|write|wrote|drafting|draft|drafted|composing|composed|building|build|built|" +
        @"creating|create|created|planning|plan|planned|making|make|made|outlining|listing|" +
        @"summari[sz]ing|revising|rewriting|expanding|translating|implementing|brainstorming|" +
        @"describing|explaining|working\s+on)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfReportedWork();

    /// <summary>Marks that the deliverable is being asked of <em>her</em>, whoever is doing what.</summary>
    [GeneratedRegex(
        @"\b(?:can|could|would|will)\s+you\b|\bplease\b|\bfor me\b" +
        @"|\b(?:i'?d like|i'?d love|i want|i need)\s+(?:you\s+to|a|an|some|the|your)\b" +
        @"|\b(?:help|give|show|make|write|send|walk|tell)\s+me\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestCue();

    /// <summary>
    /// Closing courtesies — the shape of a reply that has finished. Used to overrule the completion
    /// judge, which cannot see that a reply already said goodbye and so kept asking for more,
    /// producing four complete answers with four sign-offs concatenated into one turn.
    /// </summary>
    [GeneratedRegex(
        @"(?:let me know|hope (?:this|that) helps|hope it helps|good luck|best of luck|" +
        @"happy (?:to help|planning|writing|building)|feel free to|i'?m (?:always )?here|" +
        @"if you (?:have|'ve got) any (?:other |more |further )?questions|" +
        @"anything else i can|glad to help|you'?ve got this)" +
        @"[^.!?\n]{0,80}[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignOff();

    // Characters that end a finished thought. Anything else at the end reads as mid-sentence.
    // Includes markdown closers: "**bold**", "_emphasis_", a table row's trailing '|', "~~done~~".
    private const string TerminalPunctuation = ".!?…\"')]}”’`*_|~";

    public static bool IsDeliverableRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Someone telling you what they are working on has not asked you for it. Only an explicit
        // cue that the work is wanted from her overrides that reading.
        if (SelfReportedWork().IsMatch(message) && !RequestCue().IsMatch(message))
            return false;

        if (DeliverableVerbs().IsMatch(message))
            return true;

        foreach (var phrase in DeliverablePhrases)
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Whether the reply has already signed off. A finished reply that ends "let me know if you'd
    /// like more input" is not a reply that needs continuing, whatever a small judge model thinks.
    /// </summary>
    public static bool LooksFinished(string? reply)
        => !string.IsNullOrWhiteSpace(reply) && SignOff().IsMatch(reply.TrimEnd());

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

        if (TerminalPunctuation.Contains(last))
            return false;

        // An emoji ends a thought too: a reply that signs off with 😉 is finished, not cut off.
        // Most emoji are surrogate pairs; the rest (❤, ☺, …) land in the symbol categories.
        if (char.IsSurrogate(last) || char.GetUnicodeCategory(last) is UnicodeCategory.OtherSymbol)
            return false;

        // Ends mid-sentence — no terminal punctuation.
        return true;
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
