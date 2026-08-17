using Companion.Core.Abstractions;

namespace Companion.Tests.Fixtures;

/// <summary>A chat model that always returns a fixed string — used to test parsing and streaming.</summary>
public sealed class CannedChatModel : IChatModel
{
    private readonly string _response;

    public CannedChatModel(string response) => _response = response;

    public Task<ChatCompletion> CompleteAsync(
        string systemPrompt, string userMessage, ResponseFormat? format = null,
        string? assistantPrefix = null, CancellationToken ct = default)
        => Task.FromResult(ChatCompletion.FromText(_response));

    public Task<ChatCompletion> StreamAsync(
        string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        // Emit the canned response as a few chunks so streaming can be exercised.
        var words = _response.Split(' ');
        for (var i = 0; i < words.Length; i++)
            sink.Report(i < words.Length - 1 ? words[i] + " " : words[i]);
        return Task.FromResult(ChatCompletion.FromText(_response));
    }
}
