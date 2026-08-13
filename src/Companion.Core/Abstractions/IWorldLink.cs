using Companion.Core.Services;

namespace Companion.Core.Abstractions;

/// <summary>Something that happened somewhere she is, as the world reported it.</summary>
/// <param name="Kind">"arrived", "presence", or "refusal" — whatever the world chose to say.</param>
/// <param name="Body">Who it concerns. "ava" is her; anything else is somebody else.</param>
/// <param name="Place">Where, when the event has a location.</param>
/// <param name="Text">A plain sentence, for logs and — later — for reflection to read.</param>
public sealed record WorldPerception(
    DateTimeOffset At, string Kind, string? Body, string? Place, string Text);

/// <summary>
/// A connection to a world she can be in — and nothing more than a connection.
///
/// The design rule this exists to honour: <b>the companion may hold a connection, but never a
/// model.</b> There is no place table here, no position, no persisted anything. The world says
/// what exists each time it is connected to, and <see cref="Places"/> is what it last said. Unplug
/// the world and the companion's database contains nothing that refers to it.
///
/// Not being connected is a perfectly normal state, not an error. She simply isn't anywhere.
/// </summary>
public interface IWorldLink
{
    /// <summary>True when a world is configured at all. False means this companion has no world.</summary>
    bool Configured { get; }

    /// <summary>True when currently connected and authenticated.</summary>
    bool Connected { get; }

    /// <summary>
    /// What the world last said exists. Empty when not connected — deliberately, because a
    /// remembered menu is the first step toward keeping a model of somewhere else.
    /// </summary>
    IReadOnlyList<WorldPlace> Places { get; }

    /// <summary>Where the world last said she is, or null if it hasn't said.</summary>
    string? CurrentPlace { get; }

    /// <summary>Asks her to go somewhere. A place name from <see cref="Places"/>, never a direction.</summary>
    Task<bool> GoToAsync(string placeId, CancellationToken ct = default);

    /// <summary>Raised for everything the world reports. Handlers must not throw.</summary>
    event Action<WorldPerception>? Perceived;
}
