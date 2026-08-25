using Companion.Core.Activities;

namespace Companion.Core.Abstractions;

/// <summary>
/// Supplies the turn's ACTIVE activity instance, when there is one.
///
/// This seam exists because the readiness audit found the activity contributor wired nowhere:
/// Sources 1a/1b built the runtime, the strategy and the shadow store, and nothing ever
/// instantiated an instance per turn. The contributor is now connected to the native assembly
/// through this interface, so it participates the moment a producer exists.
///
/// There is deliberately no production implementation yet. Procedures reach the turn today
/// only as retrieved PROSE notes, and building an ActivityInstance out of those would be
/// parsing prose back into structure — the one move the whole protocol exists to prevent.
/// </summary>
public interface IActivityInstanceProvider
{
    Task<ActivityInstance?> GetActiveAsync(
        string userId, Guid conversationId, CancellationToken ct = default);
}
