namespace Companion.Core.Abstractions;

/// <summary>
/// Decides whether a user message is too private or sensitive to feed into durable derived memory
/// systems such as extraction, anticipations, project updates, emotional signals, or commitments.
/// </summary>
public interface IPrivacyClassifier
{
    Task<bool> ShouldSkipDerivedMemoryAsync(string userMessage, CancellationToken ct = default);
}
