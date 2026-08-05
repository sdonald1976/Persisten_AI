using Companion.Core.Abstractions;

namespace Companion.Tests.Fixtures;

/// <summary>A chat model that always returns a fixed string — used to test the LLM extractor's parsing.</summary>
public sealed class CannedChatModel : IChatModel
{
    private readonly string _response;

    public CannedChatModel(string response) => _response = response;

    public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        => Task.FromResult(_response);
}
