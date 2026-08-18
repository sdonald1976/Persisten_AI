using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The roaming policy that ships: <see cref="RoamingPolicy"/>, behind the interface.
///
/// A thin wrapper on purpose. It holds one thing the observation does not — how long she can be
/// somewhere before staying becomes its own reason to leave — because that is a tuning knob about
/// how a person behaves at home rather than a fact about her situation, and mixing the two would
/// mean a learned policy receiving a hyperparameter of the rule it replaced.
/// </summary>
public sealed class HeuristicRoamingPolicy : IRoamingPolicy
{
    private readonly TimeSpan _restlessAfter;

    public HeuristicRoamingPolicy(TimeSpan? restlessAfter = null)
        => _restlessAfter = restlessAfter is { } given && given > TimeSpan.Zero ? given : RoamingPolicy.Restless;

    public string Name => "heuristic";

    public RoamingDeliberation Deliberate(RoamingObservation observation)
        => RoamingPolicy.Deliberate(observation, _restlessAfter);
}
