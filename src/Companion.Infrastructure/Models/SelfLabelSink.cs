using System.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Strips her own name off the front of a reply <em>as it streams</em>.
///
/// Cleaning the finished text is not enough, because the finished text is not what the user sees.
/// The web client builds the bubble from the streamed tokens and then keeps them
/// (<c>finalText = currentBot.textContent</c>), discarding the cleaned reply frame; the same
/// tokens are fed to speech synthesis sentence by sentence as they arrive. So a label removed
/// afterwards would still be read on screen and still be spoken aloud — corrected everywhere
/// except the two places a person actually experiences.
///
/// It holds back the opening few characters, decides once, and is a pass-through for the rest of
/// the reply. The cost is a fraction of a word of latency before the first paint; the alternative
/// is her introducing herself, by name, at the start of every message.
/// </summary>
internal sealed class SelfLabelSink : IProgress<string>
{
    /// <summary>
    /// How much to see before deciding. Long enough that a label is always complete along with
    /// some of what follows it — "Ava: " is five characters — and short enough not to be a visible
    /// stall.
    /// </summary>
    private const int Window = 40;

    private readonly IProgress<string> _inner;
    private readonly string? _speaker;
    private StringBuilder? _pending = new();

    public SelfLabelSink(IProgress<string> inner, string? speaker)
    {
        _inner = inner;
        _speaker = speaker;
    }

    public void Report(string value)
    {
        if (_pending is null)
        {
            _inner.Report(value);
            return;
        }

        _pending.Append(value);
        if (_pending.Length < Window)
            return;

        Flush();
    }

    /// <summary>
    /// Emits whatever is still held back. Must be called when the round ends, or a reply shorter
    /// than the window would never be delivered at all.
    /// </summary>
    public void Flush()
    {
        if (_pending is null)
            return;

        var text = PromptEchoFilter.TrimSelfLabel(_pending.ToString(), _speaker);
        _pending = null;
        if (text.Length > 0)
            _inner.Report(text);
    }
}
