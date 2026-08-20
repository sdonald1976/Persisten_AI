namespace Companion.Core.Domain;

/// <summary>A question the companion asked that no later user turn has addressed.</summary>
/// <param name="Question">The trailing question, verbatim.</param>
/// <param name="MessagesAgo">How many messages back it was asked (1 = the previous message).</param>
public sealed record OpenQuestionState(string Question, int MessagesAgo);

/// <summary>
/// The turn's reference resolution as extraction needs to know it. "exact" (an enumerated
/// item, the user's own prior message) and "unambiguous" (a pronoun with exactly one
/// user-introduced candidate) are CONSUMABLE: the extractor is told, and the stored fact
/// cites both the current utterance and the one that introduced the referent. "guess" is the
/// opposite of consumable — it is a warning: the user's message contains a reference the
/// system could not pin, so a candidate that names a person the user did not name this turn
/// is somebody's invention (the first live run proved whose: the chat model's own reply
/// guessed a name and the extractor laundered it into a fact). The extractor never sees a
/// guess; the pipeline uses it to veto.
/// </summary>
public sealed record ReferenceResolution(
    string Marker,
    string Referent,
    ResolutionConfidence Confidence,
    Guid? SourceMessageId,
    string? SourceExcerpt)
{
    public bool Consumable => Confidence is ResolutionConfidence.Exact or ResolutionConfidence.Unambiguous;
}

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

    /// <summary>What this turn IS. Typed — a move is cognition, not language — while every
    /// JSON/diagnostic boundary still reads the kebab label ("answers-open-question"). The
    /// property-level converter is deliberate: the API registers a global
    /// JsonStringEnumConverter, and property attributes are the one precedence tier above
    /// options converters.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(KebabEnumConverter<ConversationMove>))]
    public required ConversationMove Move { get; init; }

    /// <summary>What a detected reference resolved to, when it did ("the second one" → the
    /// enumerated item's text). Null when nothing resolved.</summary>
    public string? ResolvedReference { get; init; }

    /// <summary>How the referent was chosen. Null when nothing resolved. Extraction consumes
    /// only Exact/Unambiguous; retrieval may use all three.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(KebabEnumConverter<ResolutionConfidence>))]
    public ResolutionConfidence? ResolutionConfidence { get; init; }

    /// <summary>The message that introduced the referent, when identifiable — provenance for
    /// any durable fact extracted through this resolution.</summary>
    public Guid? ReferentSourceMessageId { get; init; }

    /// <summary>Bounded text of that source message.</summary>
    public string? ReferentSourceExcerpt { get; init; }

    /// <summary>The question the current turn answered, when <see cref="Move"/> says it did.</summary>
    public string? BoundQuestion { get; init; }

    /// <summary>When <see cref="Move"/> is Correction: whose error is being corrected.
    /// Self-correction markers ("I meant…") make it the user's; otherwise a correction
    /// following the companion's own message targets her. The field that makes
    /// "we both slipped up" a checkable violation.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(KebabEnumConverter<ErrorOwner>))]
    public ErrorOwner? CorrectionTarget { get; init; }

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
