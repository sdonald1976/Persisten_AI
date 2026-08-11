namespace Companion.Core.Abstractions;

/// <summary>
/// A way for the companion to reach the user OUTSIDE a conversation — a push notification today,
/// possibly SMS/email later. One method on purpose: outreach is a short message from her, not a
/// delivery platform.
/// </summary>
public interface IOutboundChannel
{
    /// <summary>False when the channel isn't set up — outreach silently stays off.</summary>
    bool Configured { get; }

    /// <summary>Delivers one message. Returns false on failure (the caller decides what that means).</summary>
    Task<bool> SendAsync(string title, string body, CancellationToken ct = default);
}
