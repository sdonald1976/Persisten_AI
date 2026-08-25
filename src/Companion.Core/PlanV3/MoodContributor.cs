using Companion.Core.Domain;

namespace Companion.PlanV3;

/// <summary>
/// Source 4b: AVA'S OWN internal modulation votes on register. The input is her
/// <see cref="CompanionStateSnapshot"/> and nothing else — never <c>MoodReading</c>, never
/// <c>EmotionalSignal</c>, never any inference about how the USER feels. Two different things
/// wear the name "mood" in this codebase; this contributor touches only hers.
///
/// It modulates and nothing more. Zero item grants means it cannot make a claim, cannot
/// restrict, cannot imply consent, cannot create a preference, and cannot produce a recitable
/// explanation of why she sounds the way she does. Her mood colours delivery; it never becomes
/// something she says.
///
/// Provenance is a real transition event: the vote cites
/// <see cref="CompanionStateSnapshot.StateRef"/>, the id of the row in
/// <c>CompanionMoodTransitions</c> that produced this reading. Without one, it does not vote —
/// an unciteable mood has no standing.
/// </summary>
public sealed class MoodContributor(CompanionStateSnapshot state) : IPlanV3Contributor
{
    /// <summary>
    /// The declared floor. Spirits nearer contentment than this contribute NOTHING — not a
    /// "neutral" vote, which would still displace a lower-ranked source, but silence. Decay
    /// carries every mood back under this line on its own, so a moment that once moved her
    /// stops modulating her by itself, without anything having to expire it.
    /// </summary>
    public const double Floor = 0.3;

    public string SourceId => "mood";

    /// <summary>Why this reading did or did not vote. Tokens only — never prose, never a number
    /// that could reconstruct how she feels.</summary>
    public string Outcome { get; private set; } = "not-run";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        // No transition to point at = no provenance = no vote.
        if (state.StateRef is not { } stateRef)
        {
            Outcome = "no-state-ref";
            return PlanContributionResult.Empty;
        }

        var value = Intensity(state.Spirits);
        if (value is null)
        {
            Outcome = "below-floor";
            return PlanContributionResult.Empty;
        }

        Outcome = $"voted-{value}";
        return new PlanContributionResult(
            [],
            [
                new RegisterProposal(
                    "intensity",
                    value,
                    "mood.spirits",
                    new Provenance(Origin: "derived", EvidenceRef: stateRef.ToString()),
                    Restrictive: false),
            ]);
    }

    /// <summary>
    /// The complete mapping: spirits → intensity, three outcomes, bounded by
    /// <see cref="Floor"/>. Deliberately NOT warmth — warmth toward the user is a persona and
    /// relationship concern, and sourcing it from her spirits would make her low mood the
    /// user's problem, which is exactly what the state's own contract forbids.
    /// </summary>
    public static string? Intensity(double spirits) => spirits switch
    {
        <= -Floor => "flat",
        >= Floor => "raised",
        _ => null,
    };
}
