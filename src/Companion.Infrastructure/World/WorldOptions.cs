namespace Companion.Infrastructure.World;

/// <summary>
/// Where her world is, if she has one. Bound from the "World" section.
///
/// Absent by default: a companion with no world is the normal case, and nothing about her should
/// depend on having one.
/// </summary>
public sealed class WorldOptions
{
    public const string SectionName = "World";

    /// <summary>
    /// The world's wire address, e.g. <c>ws://192.168.1.20:8738</c>. Empty means no world, which
    /// disables everything here rather than causing an error.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// The world's access token. The same secret the world generated beside its own state file.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// How often she reconsiders where she wants to be. Not how often she moves — the policy has
    /// its own threshold for that, and mostly decides to stay put.
    /// </summary>
    public double RoamCheckMinutes { get; set; } = 2;

    /// <summary>
    /// How long she can be in one room before staying becomes its own reason to leave.
    ///
    /// Her mood changes only a few times a day, so without this she settles into whichever room
    /// suits the hour and does not move again — at rest, but indistinguishable from inert to
    /// anyone looking in. Zero switches it off entirely.
    /// </summary>
    public double RestlessMinutes { get; set; } = 45;

    /// <summary>How long to wait before reconnecting after the world goes away.</summary>
    public double ReconnectSeconds { get; set; } = 15;

    /// <summary>
    /// Whether she may move on her own. Off means the connection is still made and perception
    /// still flows, but nothing sends her anywhere — useful for watching what she would do.
    /// </summary>
    public bool Roam { get; set; } = true;

    public bool Configured => !string.IsNullOrWhiteSpace(Url);
}
