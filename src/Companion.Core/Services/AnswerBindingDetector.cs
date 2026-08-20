using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>The detected relationship: the user's short message answers the question the
/// companion asked one turn earlier.</summary>
/// <param name="Question">The trailing question from the companion's previous message.</param>
/// <param name="Answer">The user's message, verbatim.</param>
public sealed record AnswerBinding(string Question, string Answer);

/// <summary>
/// Detects the one conversational shape the live tests proved the chat model gets wrong on its
/// own: the companion asks a question, the user answers with a short elliptical reply
/// ("Additive."), and the model — with the whole transcript in front of it — reinterprets the
/// reply as a new topic instead of as the answer. The question was present in the prompt; what
/// was missing was AUTHORITY over the interpretation. This rule supplies it: when it fires, the
/// packet says explicitly what the user's message is answering, and retrieval searches the
/// question + answer together instead of embedding a near-anchorless fragment alone.
///
/// Deliberately conservative and deliberately deterministic. The ToolNudge lesson
/// (F1 0.778 on imagined phrasings, 0.087 on real ones — SPECIALIST_MODELS.md) applies to every
/// heuristic in this codebase: this one's verdict is recorded as a per-turn decision and
/// captured for corpus review, so its real-world hit rate gets measured instead of assumed.
/// </summary>
public static class AnswerBindingDetector
{
    /// <summary>
    /// A reply longer than this is prose, not an elliptical answer — the model handles full
    /// sentences fine; it is fragments that get reinterpreted.
    /// </summary>
    private const int MaxAnswerChars = 80;

    /// <summary>
    /// The tighter bound, and the one that does the real work: elliptical answers are a few
    /// words ("Additive.", "the red one", "probably Tuesday"). A short full clause ("I got the
    /// pump running") is a statement the model reads fine on its own — the first cut of this
    /// rule used characters alone and bound "Never mind that — I finally got the irrigation
    /// pump running", which is a topic change, not an answer.
    /// </summary>
    private const int MaxAnswerWords = 6;

    /// <summary>A "question" longer than this is a pasted block, not a question to bind to.</summary>
    private const int MaxQuestionChars = 200;

    /// <summary>
    /// The companion's previous message, when it ends with a question — the precondition for a
    /// binding, and also the capture gate: turns in this situation are the ones whose base rate
    /// needs measuring, whether or not the rule below fires.
    /// </summary>
    public static string? TrailingQuestion(IReadOnlyList<Message> recent)
    {
        if (recent.Count == 0)
            return null;
        var last = recent[^1];
        return last.Role == MessageRole.Assistant ? TrailingQuestion(last.Content) : null;
    }

    /// <summary>
    /// Binds a short, non-interrogative user message to the companion's immediately preceding
    /// trailing question. Null when the shape doesn't hold — most turns.
    /// </summary>
    public static AnswerBinding? Detect(IReadOnlyList<Message> recent, string userMessage)
    {
        var question = TrailingQuestion(recent);
        if (question is null)
            return null;

        var answer = userMessage.Trim();
        if (answer.Length == 0 || answer.Length > MaxAnswerChars)
            return null;
        if (answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaxAnswerWords)
            return null;

        // A reply that asks its own question is a turn of its own, not an answer.
        if (answer.Contains('?'))
            return null;

        return new AnswerBinding(question, answer);
    }

    /// <summary>
    /// The last sentence of the text, when the text ends with a question mark. A question asked
    /// mid-message and then talked past is not treated as open — only a question the companion
    /// LEFT hanging binds the next reply. Public because WorkingContext uses the same reading
    /// to track open questions across the window.
    /// </summary>
    public static string? TrailingQuestion(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0 || trimmed[^1] != '?')
            return null;

        var start = trimmed.LastIndexOfAny(['.', '!', '?', '\n'], trimmed.Length - 2);
        var question = trimmed[(start + 1)..].Trim();
        return question.Length > 0 && question.Length <= MaxQuestionChars ? question : null;
    }
}
