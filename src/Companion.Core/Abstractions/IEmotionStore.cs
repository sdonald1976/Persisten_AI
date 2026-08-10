using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The authoritative, append-only log of emotional readings. Like every store here it enforces
/// user isolation. It is write-once per reading — the companion never edits how a moment felt — and
/// the relationship's evolving state is always derived from these rows, never stored separately.
/// </summary>
public interface IEmotionStore
{
    /// <summary>Appends one emotional reading.</summary>
    Task AddSignalAsync(EmotionalSignal signal, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent readings for a user, newest first, capped at <paramref name="count"/>.
    /// Includes followed-up signals — the caller decides what still counts as an open concern.
    /// </summary>
    Task<IReadOnlyList<EmotionalSignal>> GetRecentSignalsAsync(
        string userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Marks every open signal about <paramref name="topic"/> (case-insensitive) as followed-up, so
    /// the concern stops being surfaced. Returns how many were closed. Used both when the companion
    /// asks about a topic and when a newer feeling about the same topic supersedes the old one.
    /// </summary>
    Task<int> MarkTopicFollowedUpAsync(string userId, string topic, CancellationToken ct = default);
}
