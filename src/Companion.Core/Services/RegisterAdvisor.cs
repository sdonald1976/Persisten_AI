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
    private const int BriefThreshold = 30;
    private const int ConversationalThreshold = 200;

    /// <summary>Reply-shape guidance for the prompt, or null when long-form rules should govern.</summary>
    public static string? Advise(string? userMessage)
    {
        var text = (userMessage ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        if (text.Length <= BriefThreshold)
            return "Their message is short and casual — match its energy. One or two sentences is a " +
                   "complete reply; even a few words can be. No lists, no headers, and don't tack a " +
                   "question onto the end out of habit.";

        if (text.Length <= ConversationalThreshold)
            return "Keep this reply conversational — a few natural sentences, no padding. End with a " +
                   "question only if you genuinely need the answer.";

        return null; // substantial message → the standing long-form guidance applies
    }
}
