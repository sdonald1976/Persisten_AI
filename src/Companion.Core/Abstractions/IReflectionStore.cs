using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Persists the companion's private diary (reflections) and the curiosities minted from it.
/// Like every store here it enforces user isolation. Reflections are append-only — the companion
/// never rewrites a past thought; curiosities only move forward through their lifecycle
/// (Open → Voiced/Dismissed), they are never reopened.
/// </summary>
public interface IReflectionStore
{
    /// <summary>Appends one reflection and its curiosities in a single unit.</summary>
    Task AddAsync(Reflection reflection, IReadOnlyList<Curiosity> curiosities, CancellationToken ct = default);

    /// <summary>The newest reflection (musing or watermark-only), or null if none exist yet.</summary>
    Task<Reflection?> GetLatestAsync(string userId, CancellationToken ct = default);

    /// <summary>The newest reflections, newest first, capped at <paramref name="count"/>.</summary>
    Task<IReadOnlyList<Reflection>> GetRecentAsync(string userId, int count, CancellationToken ct = default);

    /// <summary>All open (not yet voiced/dismissed) curiosities, newest first.</summary>
    Task<IReadOnlyList<Curiosity>> GetOpenCuriositiesAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// The next curiosity worth voicing right now, or null. Returns the newest open one — but only
    /// when no other curiosity has been voiced within <paramref name="cooldown"/> of
    /// <paramref name="now"/>, so consecutive turns never each raise a fresh question and the
    /// companion's curiosity stays a spark, not an interrogation.
    /// </summary>
    Task<Curiosity?> GetNextToVoiceAsync(
        string userId, DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default);

    /// <summary>Marks a curiosity voiced (ownership-scoped; a foreign or unknown id is a no-op).</summary>
    Task MarkVoicedAsync(string userId, Guid curiosityId, DateTimeOffset now, CancellationToken ct = default);
}
