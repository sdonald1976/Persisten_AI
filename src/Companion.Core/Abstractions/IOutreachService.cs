using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The decision-and-delivery for the companion reaching out on its own. One call weighs the gates
/// (is she set up to reach out, has the user actually been away, is the budget clear, is it a
/// decent hour, is there something real to say) and either sends one message or does nothing.
/// </summary>
public interface IOutreachService
{
    /// <summary>Considers one outreach now. Returns the sent message, or null when any gate held it back.</summary>
    Task<OutboundMessage?> RunOnceAsync(string userId, CancellationToken ct = default);
}
