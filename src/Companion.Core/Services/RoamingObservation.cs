using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Everything a roaming policy is allowed to see, in one value.
///
/// The point of gathering it into a record is that it can be handed to something other than the
/// rule that exists today — a learned policy, a shadow comparison, a replay of a decision that
/// looked wrong — without each of those growing its own idea of what "her situation" means. A
/// method with seven positional parameters is a seam nobody can plug into.
///
/// <b>What is deliberately not here, and why.</b> The brief this was built from listed a longer
/// set of inputs: user presence, recent experiences, novelty, environment state, social state. They
/// are absent because the caller cannot supply them today, and a field that is always null is worse
/// than a missing one — it reads as available, gets consumed, and quietly means nothing. Adding any
/// of them is a change to what the world worker gathers, which is a different piece of work from
/// making the policy replaceable.
///
/// One absence is not a gap but a decision. <b>Concerns</b> — things in the world that need doing —
/// never reach a policy at all: the worker acts on them before it asks where she would rather be,
/// because a need is not a preference. That rule exists because feeding concerns in as ordinary
/// preoccupations made a stove going cold score 0.5 against the study's 0.4, a gap under the move
/// threshold, so she sat and read while the fire went out. It stays above the seam. Models judge
/// where she would like to be; code decides that something needing doing outranks it.
/// </summary>
/// <param name="Places">What the world just advertised. Empty means there is nowhere to go.</param>
/// <param name="CurrentPlace">Where she is now, if the world said.</param>
/// <param name="PreviousPlace">Where she was before that — one step of history, enough to stop her pacing between two doors.</param>
/// <param name="State">Her spirits and energy.</param>
/// <param name="Preoccupations">What is on her mind, as free text. Open curiosities today.</param>
/// <param name="TimeInPlace">How long she has been where she is, or null if unknown.</param>
/// <param name="At">When this is being decided. Free, and the one thing a learned policy would certainly want that the heuristic ignores.</param>
public sealed record RoamingObservation(
    IReadOnlyList<WorldPlace> Places,
    string? CurrentPlace,
    string? PreviousPlace,
    CompanionStateSnapshot State,
    IReadOnlyList<string> Preoccupations,
    TimeSpan? TimeInPlace = null,
    DateTimeOffset? At = null);

/// <summary>
/// What a policy concluded, and everything it considered on the way there.
///
/// The ranking is kept rather than only the winner, because the winner alone cannot be compared.
/// Every question worth asking about a policy — did the new one rank the same places in the same
/// order, was the choice close or obvious, why did she stay — needs the losers too. It is the same
/// reason memory retrieval reports what it excluded.
/// </summary>
/// <param name="Ranked">Every place scored, best first. Ties resolve the same way every time.</param>
/// <param name="Move">Where she is going, or null to stay put.</param>
/// <param name="Reason">Why — including why she stayed, which is the answer most often needed and least often recorded.</param>
/// <param name="MoveThreshold">How much better somewhere else had to be. Part of the record because it changes as she settles.</param>
public sealed record RoamingDeliberation(
    IReadOnlyList<RoamingChoice> Ranked,
    RoamingChoice? Move,
    string Reason,
    double MoveThreshold)
{
    /// <summary>The best place she could go, whether or not it was worth getting up for.</summary>
    public RoamingChoice? Best => Ranked.Count > 0 ? Ranked[0] : null;

    /// <summary>
    /// How much better the best option was than staying — the margin the threshold was compared
    /// against. Zero when she is already in the best place, and the number to look at first when a
    /// policy seems too restless or too inert.
    /// </summary>
    public double Margin { get; init; }
}
