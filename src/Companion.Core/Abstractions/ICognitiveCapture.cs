namespace Companion.Core.Abstractions;

/// <summary>
/// Records the heuristics' verdicts on real sentences, for the corpus a future model is judged on.
///
/// Separate from <see cref="IShadowRecorder"/> because they answer different questions and are
/// switched on for different reasons: shadow mode asks "does this model agree with the rule it
/// would replace", and needs a model; capture asks "what does the rule actually do out here", and
/// needs only the rule. Capture is what you turn on when there is no model yet, which — for the
/// cognitive classifier — is today.
/// </summary>
public interface ICognitiveCapture
{
    /// <summary>Whether anything is being recorded, so callers can skip the work entirely.</summary>
    bool IsCapturing { get; }

    /// <summary>Records the classifier-shaped judgements made about one user message.</summary>
    Task CaptureUserMessageAsync(
        string message, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null);

    /// <summary>Records the judgements made about the companion's own reply.</summary>
    Task CaptureReplyAsync(
        string reply, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null);

    /// <summary>
    /// Records one supersession pair at the moment the pipeline decided it — the input the
    /// specialised model will be trained and judged on, with the incumbent's outcome as its weak
    /// label. See <see cref="SupersessionPairCapture"/> for what a row carries and why.
    /// </summary>
    Task CapturePairAsync(SupersessionPairCapture pair, CancellationToken ct = default);
}

/// <summary>
/// One supersession decision, captured as the pair the pipeline actually judged.
///
/// This is the §2 input schema of <c>docs/SUPERSESSION_TASK.md</c> made concrete. Message-level
/// capture cannot produce it: the decision is about an (incoming, existing) pair, and only the
/// pipeline knows which existing memory was in play, what cardinality said, and what the code
/// decided. Everything a future adjudicator or model needs travels in the row; nothing else does —
/// the full user message is deliberately absent (the evidence excerpts are what the wording signal
/// actually reads), and no similarity score is a model input, only provenance.
/// </summary>
/// <param name="IncomingFact">The candidate's normalized fact.</param>
/// <param name="IncomingValue">The candidate's slot value.</param>
/// <param name="Predicate">The closed-vocabulary predicate.</param>
/// <param name="Utterance">The user's own words the fact was extracted from — the evidence
/// excerpts, which is where replacement signals live and normalized facts lose them.</param>
/// <param name="ExistingId">Stable reference to the existing memory, so a later /forget can find
/// this row and an adjudicator can find the memory.</param>
/// <param name="ExistingFact">The existing memory's normalized fact.</param>
/// <param name="ExistingValue">The existing memory's slot value.</param>
/// <param name="ExistingPredicate">The existing memory's predicate (may differ across slots).</param>
/// <param name="ExistingAgeDays">Days since the existing memory was first observed — a same-day
/// conflict reads as a correction, a two-year gap as a change.</param>
/// <param name="ExistingConfirmedDays">Days since it was last confirmed.</param>
/// <param name="SameSlot">Whether the two occupy one slot key.</param>
/// <param name="SingleValued">PredicateVocabulary's answer for the incoming predicate.</param>
/// <param name="Similarity">Embedding similarity, carried as provenance for analysis — measured
/// unable to order these cases, so it is never a model input.</param>
/// <param name="IncumbentOutcome">What the shipped rules decided, in the pipeline's own terms:
/// duplicate | duplicate:similar | supersedes:single_valued | supersedes:wording |
/// needs_review:single_valued | needs_review:wording | coexist.</param>
public sealed record SupersessionPairCapture(
    string IncomingFact,
    string? IncomingValue,
    string? Predicate,
    string Utterance,
    Guid ExistingId,
    string ExistingFact,
    string? ExistingValue,
    string? ExistingPredicate,
    int ExistingAgeDays,
    int ExistingConfirmedDays,
    bool SameSlot,
    bool SingleValued,
    double Similarity,
    string IncumbentOutcome,
    /// <summary>Whose memory this is. Required for A3 ownership on the captured row.</summary>
    string? UserId = null);
