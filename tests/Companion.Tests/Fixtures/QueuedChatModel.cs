using Companion.Core.Abstractions;

namespace Companion.Tests.Fixtures;

/// <summary>
/// A chat model that replays a scripted queue of responses and records every prompt it saw.
/// When the queue runs dry it returns a fixed fallback, so an unexpected extra call degrades
/// into an assertable state instead of throwing from inside the service under test.
/// </summary>
public sealed class QueuedChatModel : IChatModel
{
    private readonly Queue<string> _responses;
    private readonly string _fallback;

    public List<string> SystemPrompts { get; } = new();
    public List<string> UserMessages { get; } = new();
    public int Calls { get; private set; }

    public QueuedChatModel(IEnumerable<string> responses, string fallback = "(no more scripted replies)")
    {
        _responses = new Queue<string>(responses);
        _fallback = fallback;
    }

    public QueuedChatModel(params string[] responses) : this(responses, "(no more scripted replies)") { }

    public Task<ChatCompletion> CompleteAsync(
        string systemPrompt, string userMessage, bool jsonMode = false,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        Calls++;
        SystemPrompts.Add(systemPrompt);
        UserMessages.Add(userMessage);
        var text = _responses.Count > 0 ? _responses.Dequeue() : _fallback;
        return Task.FromResult(ChatCompletion.FromText(text, model: "queued"));
    }

    public async Task<ChatCompletion> StreamAsync(
        string systemPrompt, string userMessage, IProgress<string> sink,
        string? assistantPrefix = null, CancellationToken ct = default)
    {
        var full = await CompleteAsync(systemPrompt, userMessage, jsonMode: false, assistantPrefix, ct);
        sink.Report(full.Text);
        return full;
    }
}
