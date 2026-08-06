namespace Companion.Core.Abstractions;

/// <summary>
/// The active user for the current execution scope, derived from trusted context (a local
/// single-user session today; an authentication boundary later) — never from an untrusted API
/// request body. Application-level entry points resolve the user through this instead of
/// accepting an arbitrary user id from the caller. Lower-level persistence contracts still take
/// <c>userId</c> explicitly so ownership stays visible and hard to omit.
/// </summary>
public interface IUserContext
{
    string UserId { get; }
}

/// <summary>A fixed user context — used by the local single-user CLI/API until real auth exists.</summary>
public sealed class FixedUserContext : IUserContext
{
    public FixedUserContext(string userId) => UserId = userId;
    public string UserId { get; }
}
