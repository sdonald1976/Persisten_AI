namespace Companion.Core.Abstractions;

/// <summary>
/// Restyles a deterministic draft message into the companion's own voice — the LlmGreeter
/// pattern, generalized. The draft is always a complete, correct message on its own: whatever
/// the model does, meaning may not change, and any failure (offline, timeout, garbage output)
/// returns the draft untouched. Determinism decides WHAT to say; this only polishes HOW.
/// </summary>
public interface IVoiceRephraser
{
    /// <param name="situation">One line of framing, e.g. "a short push notification you're sending unprompted".</param>
    Task<string> RephraseAsync(string userId, string draft, string situation, CancellationToken ct = default);
}

/// <summary>The offline default: the draft IS the message.</summary>
public sealed class PassthroughRephraser : IVoiceRephraser
{
    public Task<string> RephraseAsync(string userId, string draft, string situation, CancellationToken ct = default)
        => Task.FromResult(draft);
}
