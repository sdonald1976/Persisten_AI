using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Produces a session-opening <see cref="Greeting"/> so the user doesn't have to initiate. The
/// companion speaks first, grounding its openers in what it remembers — that's the payoff of a
/// persistent memory: it can pick the thread back up for you.
/// </summary>
public interface IGreeter
{
    Task<Greeting> GreetAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Rephrases an already-grounded <see cref="Greeting"/> in the companion's own voice (a model
/// call, so potentially slow). Registered only when a real model is configured, letting a face
/// show the instant deterministic greeting first and upgrade the wording when this completes —
/// the user never stares at a blank screen while a local model cold-loads.
/// </summary>
public interface IGreetingRephraser
{
    Task<Greeting> RephraseAsync(Greeting grounded, CancellationToken ct = default);
}
