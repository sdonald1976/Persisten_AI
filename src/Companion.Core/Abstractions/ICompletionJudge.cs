namespace Companion.Core.Abstractions;

/// <summary>
/// Decides whether an assistant reply that stopped <em>on its own</em> (not cut off by the token
/// limit) has actually finished what the user asked for. This is the one honest way to catch a
/// small model that self-truncates — writes a chunk of a story or plan and stops — since the
/// transport reports that as a normal <c>"stop"</c>. A separate, cheap model makes the call; the
/// generator uses it only when there's enough output that "unfinished" is plausible.
/// </summary>
public interface ICompletionJudge
{
    /// <summary>
    /// True if <paramref name="replySoFar"/> is a complete response to <paramref name="userRequest"/>;
    /// false if it looks unfinished and generation should continue. Implementations must fail
    /// closed — return true (complete) on any doubt or error — so a bad judge never traps the user
    /// in runaway continuation.
    /// </summary>
    Task<bool> IsCompleteAsync(string userRequest, string replySoFar, CancellationToken ct = default);
}
