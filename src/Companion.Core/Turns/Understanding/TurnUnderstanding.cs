using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.Core.Turns.Understanding;

/// <summary>
/// What the turn was understood to MEAN, as typed sections.
///
/// This is the system's own read of the conversation — what kind of turn it is, what its
/// references point at, what it should do — held separately from anything retrieved,
/// planned, rendered or persisted.
/// </summary>
public sealed record TurnUnderstandingResult
{
    /// <summary>The deterministic read: move, topic, open questions, references.</summary>
    public required WorkingContextState Working { get; init; }

    /// <summary>What retrieval should search for — the message's meaning, not its words.</summary>
    public required string RetrievalQuery { get; init; }

    /// <summary>The note the packet carries about how this turn was interpreted.</summary>
    public string? InterpretationNote { get; init; }

    /// <summary>
    /// The reference resolution as EXTRACTION needs it. An exact resolution is consumable; a
    /// guess is carried as a warning the extractor never sees. Null when nothing resolved.
    /// </summary>
    public ReferenceResolution? ExtractionResolution { get; init; }

    /// <summary>
    /// What the turn should DO. Filled after retrieval, because the classification counts
    /// what was retrieved — which is why understanding happens in two steps rather than one,
    /// and why this section is nullable until <see cref="TurnUnderstanding.ClassifyIntent"/>
    /// has run.
    /// </summary>
    public TurnIntentState? Intent { get; init; }

    /// <summary>
    /// Decision records this stage produced, in the order it produced them. Returned rather
    /// than written, so the caller appends them at exactly the point the turn always did and
    /// the decision sequence is unchanged.
    /// </summary>
    public required IReadOnlyList<DecisionRecord> Decisions { get; init; }
}

/// <summary>
/// The second stage of a turn: interpret what the admitted message means.
///
/// It owns the working-context read, the reference resolution that extraction depends on,
/// and the intent classification — the existing cohesive block that decides what KIND of turn
/// this is before anything is retrieved, planned or said.
///
/// It owns nothing else. Not memory retrieval, project or concept lookup, Plan/4 construction,
/// prompt rendering, model calls, tool execution, post-turn effects or shadow recording.
///
/// Static and pure, because every rule it calls already is. Making it an injected service
/// would add a constructor parameter and a lifetime to something that reads its inputs and
/// returns a value — indirection bought with nothing.
///
/// It is deliberately TWO methods rather than one. Intent classification counts the memories
/// retrieval selected, so it genuinely runs after retrieval; folding it into a single call
/// would have meant either reordering the turn or passing a count that does not exist yet.
/// The seam follows the existing data dependency instead of hiding it.
/// </summary>
public static class TurnUnderstanding
{
    /// <summary>
    /// Reads the conversation, before retrieval. Deterministic and ephemeral: recent dialogue
    /// stays dialogue, and none of this is stored.
    /// </summary>
    public static TurnUnderstandingResult Read(
        IReadOnlyList<Message> recent,
        string promptText,
        string? resolvedProjectName,
        string userName,
        string companionName)
    {
        var decisions = new List<DecisionRecord>();

        var working = WorkingContext.Read(
            recent, promptText, resolvedProjectName, userName, companionName);
        decisions.Add(new DecisionRecord
        {
            Stage = "interpretation", Decider = "rule",
            Verdict = working.Move.ToKebab(),
            Reason = working.BoundQuestion
                ?? (working.ResolvedReference is null ? null
                    : $"{working.ReferenceMarkers.FirstOrDefault()} -> {working.ResolvedReference}"),
        });

        // Exact/unambiguous resolutions are consumed — the extractor is told, and the fact
        // cites both utterances. A guess is passed as a WARNING only: the extractor never
        // sees it, and the pipeline uses it to refuse candidates naming a person the user did
        // not name this turn. A guessed referent must not become an authoritative fact
        // because retrieval found it useful, and neither must the chat model's own guess.
        ReferenceResolution? extractionResolution =
            working is { ResolvedReference: { } refValue, ResolutionConfidence: { } refConfidence }
                ? new ReferenceResolution(
                    working.ReferenceMarkers.FirstOrDefault() ?? "", refValue,
                    refConfidence, working.ReferentSourceMessageId,
                    working.ReferentSourceExcerpt)
                : null;
        if (extractionResolution is not null)
        {
            decisions.Add(new DecisionRecord
            {
                Stage = "reference.extraction", Decider = "rule",
                Verdict = extractionResolution.Consumable
                    ? $"consumed-{extractionResolution.Confidence.ToKebab()}" : "withheld-guess",
                Reason = $"{working.ReferenceMarkers.FirstOrDefault()} -> {working.ResolvedReference}",
            });
        }

        return new TurnUnderstandingResult
        {
            Working = working,
            RetrievalQuery = working.RetrievalQuery,
            InterpretationNote = working.InterpretationNote,
            ExtractionResolution = extractionResolution,
            Decisions = decisions,
        };
    }

    /// <summary>
    /// Classifies what the turn should DO, after retrieval has run.
    ///
    /// In shadow: recorded and captured, and deliberately NOT given to the packet. It gains
    /// authority over generation only if the shadow data shows the classifications are
    /// useful. "unknown" means continue naturally and is preferred to a confident mistake.
    /// </summary>
    public static (TurnIntentState Intent, DecisionRecord Decision) ClassifyIntent(
        WorkingContextState working, string promptText, int retrievedCount)
    {
        var intent = TurnIntentClassifier.Classify(working, promptText, retrievedCount);
        return (intent, new DecisionRecord
        {
            Stage = "intent", Decider = "rule",
            Verdict = intent.Intent.ToKebab(),
            Reason = intent.Reason,
        });
    }
}
