namespace Companion.Core.Domain;

/// <summary>A question the companion asked that no later user turn has addressed.</summary>
/// <param name="Question">The trailing question, verbatim.</param>
/// <param name="MessagesAgo">How many messages back it was asked (1 = the previous message).</param>
public sealed record OpenQuestionState(string Question, int MessagesAgo);

/// <summary>
/// The system's explicit representation of what is happening in the current conversation,
/// derived deterministically from the recent transcript each turn. This is WORKING state, not
/// memory: it is computed, used, traced, and discarded — recent dialogue stays dialogue, and
/// nothing here is ever written to the durable stores. Its purpose is to stop delegating
/// conversational state (what was asked, what "that one" means, whether the user changed the
/// subject) to the chat model's reading of raw transcript text. See docs/LANGUAGE_ORGAN.md
/// Phase 1.
/// </summary>
public sealed record WorkingContextState
{
    /// <summary>Questions the companion asked in the visible window that remain unanswered,
    /// newest first. A question the current turn just answered is not listed.</summary>
    public IReadOnlyList<OpenQuestionState> OpenQuestions { get; init; } = Array.Empty<OpenQuestionState>();

    /// <summary>A few keywords naming what is currently being discussed — the resolved project
    /// when there is one, else content words from the last substantive user turn.</summary>
    public string? Topic { get; init; }

    /// <summary>Proper-noun-ish entities from the recent window, newest first, capped. The
    /// candidates "her" or "that one" may refer to.</summary>
    public IReadOnlyList<string> SalientEntities { get; init; } = Array.Empty<string>();

    /// <summary>Reference phrases in the user's message that depend on recent conversation
    /// ("the second one", "her", "what I said before"), as detected.</summary>
    public IReadOnlyList<string> ReferenceMarkers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// What this turn IS, as a stable string: "answers-open-question", "resolves-reference",
    /// "correction", "continues-thread", or "new-topic". Strings rather than an enum because
    /// the primary consumers are the diagnostics ring and its JSON readers.
    /// </summary>
    public required string Move { get; init; }

    /// <summary>What a detected reference resolved to, when it did ("the second one" → the
    /// enumerated item's text). Null when nothing resolved.</summary>
    public string? ResolvedReference { get; init; }

    /// <summary>The question the current turn answered, when <see cref="Move"/> says it did.</summary>
    public string? BoundQuestion { get; init; }

    /// <summary>The user's message as sent — what retrieval would have searched for before.</summary>
    public required string RawQuery { get; init; }

    /// <summary>What retrieval should actually search for, after resolving the conversational
    /// meaning. Equal to <see cref="RawQuery"/> when nothing resolved.</summary>
    public required string RetrievalQuery { get; init; }

    /// <summary>The authoritative packet note for this reading, or null when no rule was
    /// confident enough to assert one. Confidence to assert is deliberately rarer than
    /// confidence to classify.</summary>
    public string? InterpretationNote { get; init; }
}
