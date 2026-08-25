using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persistence for frame truth. User- and conversation-scoped like every store here.
///
/// Two properties this contract owes callers, both learned the hard way elsewhere in the
/// codebase: a transition applied twice must not count twice (turn retries happen), and a
/// concurrent write must lose visibly rather than clobber silently.
/// </summary>
public interface IFrameSessionStore
{
    /// <summary>The Active session for this conversation, or null.</summary>
    Task<FrameSession?> GetActiveAsync(string userId, Guid conversationId, CancellationToken ct = default);

    /// <summary>
    /// Applies one lifecycle transition. <paramref name="idempotencyKey"/> is normally the
    /// turn's trace id: replaying the same key returns the existing session without applying
    /// anything twice.
    /// </summary>
    Task<FrameWriteResult> ApplyAsync(
        FrameTransitionRequest request, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Active boundaries for a scene — what the plan cites.</summary>
    Task<IReadOnlyList<FrameBoundaryRecord>> GetActiveBoundariesAsync(
        string userId, Guid conversationId, string sceneRef, CancellationToken ct = default);

    /// <summary>Records a scene-scoped boundary the user stated. Never global.</summary>
    Task<FrameBoundaryRecord> AddBoundaryAsync(
        FrameBoundaryRecord boundary, CancellationToken ct = default);

    /// <summary>
    /// /forget, by EXACT identity: redacts boundaries whose evidence message is in
    /// <paramref name="messageIds"/>, purging the statement. Takes ids only — no strings,
    /// because a path that can compare text eventually will.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Declared retention: removes ended sessions and their boundaries past the
    /// window, across all users. Returns rows removed.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>One requested transition, with everything the session needs to record it.</summary>
public sealed record FrameTransitionRequest
{
    public required string UserId { get; init; }
    public required Guid ConversationId { get; init; }

    /// <summary>enter | continue | switch | exit — the wire spelling.</summary>
    public required string Transition { get; init; }

    /// <summary>Why, as a content-safe token from the lifecycle decision.</summary>
    public required string Cause { get; init; }

    public required DateTimeOffset At { get; init; }

    public string? SceneRef { get; init; }
    public string? CharactersJson { get; init; }
    public string? ActiveCompanionCharacterId { get; init; }
    public string? Narration { get; init; }
    public string? Continuity { get; init; }
    public string? NarratorKind { get; init; }
    public string? NarratorCharacterId { get; init; }
    public string? ViewpointCharacterId { get; init; }
    public string? Person { get; init; }

    /// <summary>Bounded verbatim evidence for the transition log.</summary>
    public string? Evidence { get; init; }
}

/// <param name="Session">The session after the write, or null when nothing applied.</param>
/// <param name="Applied">False when the idempotency key had already been seen.</param>
/// <param name="Conflicted">True when a concurrent write won and this one was refused.</param>
public sealed record FrameWriteResult(FrameSession? Session, bool Applied, bool Conflicted = false)
{
    public static FrameWriteResult AlreadyApplied(FrameSession s) => new(s, false);
    public static FrameWriteResult Wrote(FrameSession s) => new(s, true);
    public static FrameWriteResult Conflict() => new(null, false, true);
    public static FrameWriteResult Nothing() => new(null, false);
}
