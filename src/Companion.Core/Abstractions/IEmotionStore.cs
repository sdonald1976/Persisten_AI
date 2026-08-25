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

    /// <summary>
    /// Phase 0 privacy repair: redacts every signal whose evidence is identified by one of
    /// these EXACT ids — <see cref="EmotionalSignal.MessageId"/> or
    /// <see cref="EmotionalSignal.EvidenceEventId"/>, user-scoped. The signature takes IDS
    /// ONLY and deliberately no strings: there is no text comparison anywhere in this path,
    /// so a signal can never be redacted because unrelated forgotten text resembled its cue.
    ///
    /// Redaction keeps privacy-permitted metadata (timestamp, sentiment, valence, the
    /// lexicon label) and purges the user's own words. Idempotent: an already-forgotten
    /// signal is not touched again and is not counted.
    /// </summary>
    Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, IReadOnlyCollection<Guid> evidenceEventIds,
        DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// The declared retention lifecycle: deletes signals older than
    /// <paramref name="olderThan"/> outright, across all users. Called from the sleep cycle
    /// beside the other retention sweeps. Returns how many rows were removed.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
