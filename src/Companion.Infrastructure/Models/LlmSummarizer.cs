using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Summarization backed by the configured chat model: condenses several related statements
/// into one higher-level statement for memory consolidation. Behind the same interface as
/// <c>MockSummarizer</c>, so it's a DI swap only.
/// </summary>
public sealed class LlmSummarizer : ISummarizer
{
    private const string SystemPrompt =
        "You consolidate several related observations about a user into ONE concise, higher-level " +
        "statement that captures the shared pattern. Do not add facts that aren't supported. " +
        "Return only the statement, no preamble.";

    private readonly IChatModel _chat;

    public LlmSummarizer(IChatModel chat) => _chat = chat;

    public async Task<string> SummarizeAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        if (statements.Count == 0)
            return "No supporting statements.";

        var joined = string.Join("\n- ", statements);
        var result = await _chat.CompleteAsync(SystemPrompt, $"Observations:\n- {joined}", jsonMode: false, ct: ct);
        return result.Text;
    }
}
