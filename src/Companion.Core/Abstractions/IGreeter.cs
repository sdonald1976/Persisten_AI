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
