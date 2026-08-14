namespace Companion.Core.Services;

/// <summary>
/// Picks a reply <em>shape</em> for the turn from the user's own message — the fix for the most
/// robotic habit an LLM companion has: answering "lol fair" with three polished paragraphs and a
/// question. Short and casual gets short and casual back; a real conversational message gets a
/// few natural sentences; a substantial ask gets no note at all (the existing "write it through
/// to the end" rule governs long-form). Deterministic and dumb on purpose — it reads length and
/// punctuation, not meaning; the model still owns the words.
/// </summary>
public static class RegisterAdvisor
{
    /// <summary>
    /// Up to here a message is small talk. Raised from 30, which was too tight to do its job:
    /// "She snuggles and gives kisses :)" is thirty-two characters, fell into the conversational
    /// band, and was answered with nine hundred characters and four questions. The point of the
    /// brief band is chat, and chat is usually a short sentence rather than a few words.
    ///
    /// Not raised further than this. "I've been thinking about switching jobs but I'm not sure the
    /// timing is right" is seventy-seven characters and deserves more than a line back — small talk
    /// is about weight, and length is only a rough proxy for it.
    /// </summary>
    private const int BriefThreshold = 50;

    private const int ConversationalThreshold = 200;

    /// <summary>Reply-shape guidance for the prompt, or null when long-form rules should govern.</summary>
    public static string? Advise(string? userMessage)
    {
        var text = (userMessage ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        if (text.Length <= BriefThreshold)
            return "Their message is short and casual — match its energy. One or two sentences is a " +
                   "complete reply; even a few words can be. No lists, no headers, and no question " +
                   "at the end unless you genuinely want the answer — a warm remark that simply " +
                   "lands is a better reply than one that hands the work back.";

        if (text.Length <= ConversationalThreshold)
            return "Keep this reply conversational — a few natural sentences, no padding. At most " +
                   "one question, and only if you genuinely need the answer.";

        return null; // substantial message → the standing long-form guidance applies
    }
}
