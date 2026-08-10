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
    /// </summary>
    Task<IReadOnlyList<EmotionalSignal>> GetRecentSignalsAsync(
        string userId, int count, CancellationToken ct = default);
}
