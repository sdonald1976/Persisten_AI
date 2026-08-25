using Companion.Core.Domain;

namespace Companion.PlanV3;

/// <summary>
/// Source 4c: how far along the relationship actually is votes on register — and ONLY that.
/// The input is <see cref="FamiliaritySnapshot"/>, derived from two honest counts (days known,
/// messages exchanged, taking the lower read). Nothing here touches
/// <c>EmotionalSignal</c>, <c>RelationshipSnapshot</c>, or any derived claim about how the
/// user feels: those are excluded until they have expiry, confidence, correction and privacy
/// semantics, and this contributor cannot reach them by construction.
///
/// **Familiarity is not intimacy.** A long interaction history is evidence that conversations
/// happened, not that affection, trust, consent, flirtation permission, or ownership exist.
/// So the mapping only ever RESTRAINS and never grants: the newest stage holds the register
/// back, and every later stage votes nothing at all rather than handing out warmth or teasing
/// as a reward for tenure. Closeness, if it exists, is the user's to express — through the
/// persona and preference sources that carry their actual instructions.
/// </summary>
public sealed class FamiliarityContributor(FamiliaritySnapshot familiarity) : IPlanV3Contributor
{
    public string SourceId => "relationship";

    /// <summary>Why this reading did or did not vote. Stage tokens only, never prose.</summary>
    public string Outcome { get; private set; } = "not-run";

    public PlanContributionResult Contribute(PlanContributionContext context)
    {
        if (familiarity.Stage != FamiliarityStage.New)
        {
            // Acquainted, Familiar, Close: nothing. Tenure earns no register concession.
            Outcome = $"no-vote-{familiarity.Stage.ToString().ToLowerInvariant()}";
            return PlanContributionResult.Empty;
        }

        Outcome = "voted-new";
        var provenance = new Provenance(
            Origin: "derived",
            // Resolvable and content-free: the two counts the stage was computed from.
            EvidenceRef: $"familiarity:days={familiarity.DaysKnown:F0};messages={familiarity.UserMessages}");

        return new PlanContributionResult(
            [],
            [
                // Shorter, because presuming on someone you have barely met is the failure
                // mode here — not "be cold", which would be a claim about the relationship.
                new RegisterProposal("verbosity", "short", "relationship.familiarity-stage",
                    provenance, Restrictive: false),
                // Explicit rather than incidental: teasing stays off until there is history to
                // earn it. It matches the canonical default, and saying so out loud is what
                // makes the restraint auditable instead of accidental.
                new RegisterProposal("teasing", "off", "relationship.familiarity-stage",
                    provenance, Restrictive: false),
            ]);
    }
}
