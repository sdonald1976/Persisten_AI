namespace Companion.Core.Abstractions;

/// <summary>
/// Summarization role: condenses several related statements into one higher-level statement.
/// A single provider may implement this alongside the chat/embedding roles, or it can be mocked.
/// </summary>
public interface ISummarizer
{
    Task<string> SummarizeAsync(IReadOnlyList<string> statements, CancellationToken ct = default);
}
