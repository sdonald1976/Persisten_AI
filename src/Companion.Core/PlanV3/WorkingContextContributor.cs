using Companion.Core.Domain;

namespace Companion.PlanV3;

/// <summary>
/// Source 4a: the turn's own cognitive state votes on register. Deliberately the narrowest
/// contributor in the system — **verbosity only**, from two typed `ConversationMove` values,
/// and nothing else.
///
/// Forbidden inputs, by construction rather than by discipline: this class never receives
/// <see cref="WorkingContextState.InterpretationNote"/>, <c>Topic</c>,
/// <c>SalientEntities</c>, <c>RawQuery</c>, or any other prose. Its constructor takes the
/// typed fields it is allowed to read, so there is nothing here to parse even by accident.
///
/// Two rules keep it honest:
///  - a <see cref="ResolutionConfidence.Guess"/> resolution suppresses the ENTIRE
///    contribution — if the turn's reading of what the user referred to is a guess, its
///    reading of what kind of turn this is does not get to shape the reply either;
///  - the state is TURN-LOCAL and expires automatically: the contributor is built for one
///    trace and refuses to contribute to any other, so a stale reading cannot leak forward.
/// </summary>
public sealed class WorkingContextContributor : IPlanV3Contributor
{
    private readonly Guid _traceId;
    private readonly ConversationMove _move;
    private readonly ResolutionConfidence? _resolution;
    private readonly Guid? _referentSourceMessageId;

    /// <summary>
    /// Builds the contributor from the ONLY fields it may read. Taking the typed values
    /// rather than the whole <see cref="WorkingContextState"/> is the point: the prose is
    /// not in scope here, so it cannot become a vote.
    /// </summary>
    public WorkingContextContributor(
        Guid traceId,
        ConversationMove move,
        ResolutionConfidence? resolution,
        Guid? referentSourceMessageId = null)
    {
        _traceId = traceId;
        _move = move;
        _resolution = resolution;
        _referentSourceMessageId = referentSourceMessageId;
    }

    /// <summary>Convenience factory from the turn's state — reads the typed fields only.</summary>
    public static WorkingContextContributor From(Guid traceId, WorkingContextState state)
        => new(traceId, state.Move, state.ResolutionConfidence, state.ReferentSourceMessageId);

    public string SourceId => "working-context-register";

    /// <summary>Why this turn did or did not vote. Content-safe: tokens only, never text.</summary>
    public string Outcome { get; private set; } = "not-run";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        // Turn-local validity, enforced rather than assumed. State read for one turn has no
        // standing in another, and the trace id is what says which turn this is.
        if (context.TraceId != _traceId)
        {
            Outcome = "expired-different-turn";
            return PlanContributionResult.Empty;
        }

        // A guessed reference means the turn does not actually know what it is looking at.
        // Nothing from that reading gets authority — not the reference, not the move.
        if (_resolution == ResolutionConfidence.Guess)
        {
            Outcome = "suppressed-guess";
            return PlanContributionResult.Empty;
        }

        var value = Verbosity(_move);
        if (value is null)
        {
            Outcome = $"no-signal-{_move.ToKebab()}";
            return PlanContributionResult.Empty;
        }

        Outcome = $"voted-{_move.ToKebab()}";
        return new PlanContributionResult(
            [],
            [
                new RegisterProposal(
                    "verbosity",
                    value,
                    "working-context.move",
                    new Provenance(
                        Origin: "derived",
                        // Resolvable turn identity; the referent's source message when this
                        // turn actually resolved one.
                        EvidenceRef: (_referentSourceMessageId ?? _traceId).ToString()),
                    Restrictive: false),
            ]);
    }

    /// <summary>
    /// The complete mapping. Two moves, one dimension. Both are turns where a long reply is
    /// a known failure mode — over-explaining an agreement, over-apologizing a correction —
    /// and both are typed cognition, not a reading of the words. Every other move has no
    /// honest verbosity implication and therefore votes nothing.
    /// </summary>
    private static string? Verbosity(ConversationMove move) => move switch
    {
        ConversationMove.ConfirmsClaim => "short",
        ConversationMove.Correction => "short",
        _ => null,
    };
}
