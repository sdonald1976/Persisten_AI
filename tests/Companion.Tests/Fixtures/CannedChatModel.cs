using System.Runtime.CompilerServices;
using Companion.Core.Abstractions;

namespace Companion.Tests.Fixtures;

/// <summary>A chat model that always returns a fixed string — used to test parsing and streaming.</summary>
public sealed class CannedChatModel : IChatModel
{
    private readonly string _response;

    public CannedChatModel(string response) => _response = response;

    public Task<string> CompleteAsync(
        string systemPrompt, string userMessage, bool jsonMode = false, CancellationToken ct = default)
        => Task.FromResult(_response);

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt, string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Emit the canned response as a few chunks so streaming can be exercised.
        foreach (var word in _response.Split(' '))
        {
            await Task.Yield();
            yield return word + " ";
        }
    }
}
