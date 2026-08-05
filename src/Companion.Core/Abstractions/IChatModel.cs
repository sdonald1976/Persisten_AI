namespace Companion.Core.Abstractions;

/// <summary>Chat-completion role. One provider may implement several roles, but callers depend only on this.</summary>
public interface IChatModel
{
    /// <summary>Generates an assistant reply given a rendered system/context prompt and the user message.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    /// <summary>Streams the reply token-by-token (or chunk-by-chunk). Concatenated, it equals the full reply.</summary>
    IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}
