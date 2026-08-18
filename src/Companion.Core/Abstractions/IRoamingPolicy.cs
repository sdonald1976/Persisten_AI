using Companion.Core.Services;

namespace Companion.Core.Abstractions;

/// <summary>
/// Decides where she would rather be, from her own state and the menu the world just advertised.
///
/// An interface rather than a static method because the plan is for this to be learned eventually,
/// and the migration this project uses everywhere else needs two implementations to exist at once:
/// the rule, and the candidate, running on the same observation so their answers can be compared
/// before either is trusted. That is the whole reason this seam exists now — not because the
/// heuristic is wrong.
///
/// <b>What a learned policy is actually blocked on, which is not this seam.</b> It is not the
/// interface, and it is not the observation. It is that <em>nothing in this system says a roam was
/// good</em>. There is no reward: no signal that being in the greenhouse at four o'clock was better
/// than being in the study, and no way to derive one from what is recorded. Reinforcement learning
/// without a reward is not a hard problem, it is not a problem — so the honest next step is a
/// reward signal or a labelled preference, and until one exists a learned policy here would be
/// imitating the rule it replaced, at greater cost and with less explanation.
///
/// So: the seam, and a plain statement of what would have to be true before anything else plugs
/// into it.
/// </summary>
public interface IRoamingPolicy
{
    /// <summary>Which policy this is, for the log line that explains a move after the fact.</summary>
    string Name { get; }

    /// <summary>
    /// Scores every available place and says whether any of them is worth getting up for. Must be
    /// deterministic for a given observation — a move she cannot reproduce is a move she cannot
    /// explain, and explaining her movements is the entire justification for her having a world.
    /// </summary>
    RoamingDeliberation Deliberate(RoamingObservation observation);
}
