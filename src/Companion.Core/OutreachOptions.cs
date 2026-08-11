namespace Companion.Core;

/// <summary>
/// Configuration for the companion reaching out on its own — a push notification when the user
/// has been away a while and it holds something genuinely worth saying. Disabled until
/// <see cref="NtfyUrl"/> is set; every knob here is a restraint, because unprompted contact only
/// stays welcome while it's rare and real.
/// </summary>
public sealed class OutreachOptions
{
    public const string SectionName = "Outreach";

    /// <summary>
    /// Full ntfy topic URL to POST to (e.g. https://ntfy.sh/my-companion-a8f3k2). Empty = outreach
    /// off. Subscribe to the same topic in the ntfy phone/desktop app to receive her messages.
    /// </summary>
    public string? NtfyUrl { get; set; }

    /// <summary>Optional bearer token for a protected/self-hosted ntfy server.</summary>
    public string? AuthToken { get; set; }

    /// <summary>How long the user must have been away before she considers reaching out.</summary>
    public double AwayHours { get; set; } = 24;

    /// <summary>Minimum time between two outreaches — rare enough to stay special.</summary>
    public double MinHoursBetween { get; set; } = 48;

    /// <summary>Quiet hours (server-local): no messages from this hour…</summary>
    public int QuietStartHour { get; set; } = 22;

    /// <summary>…until this hour. Equal start/end disables quiet hours.</summary>
    public int QuietEndHour { get; set; } = 8;

    /// <summary>How often the background worker checks whether an outreach is due.</summary>
    public int CheckMinutes { get; set; } = 15;
}
