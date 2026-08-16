using System.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Cleans her voice <em>as it streams</em>: her own name off the front, and narrated gestures out
/// of the middle.
///
/// Cleaning the finished text is not enough, because the finished text is not what the user sees.
/// The web client builds the bubble from the streamed tokens and then keeps them
/// (<c>finalText = currentBot.textContent</c>), discarding the cleaned reply frame; the same tokens
/// are fed to speech synthesis sentence by sentence as they arrive. So anything removed afterwards
/// would still be read on screen and still be spoken aloud — corrected everywhere except the two
/// places a person actually experiences.
///
/// Two jobs, because both failures reach the stream and both were found the same way:
///
///   The label. Shown a transcript headed "[COMPANION - Ava]", the model adopts the format and
///   opens with "Ava: ". It is decided once, from the opening few characters, then never again.
///
///   The gestures. "*smiles warmly*", "*giggles softly*". There is a prompt rule against these and
///   it is not enough — measured by the soak harness, it holds on neutral conversation and
///   collapses on affectionate conversation, which is exactly when a roleplay fine-tune pulls
///   hardest. These can appear anywhere in a reply, so the whole stream is watched: an asterisk
///   opens a held span, and the span is judged only once it closes.
/// </summary>
internal sealed class SelfLabelSink : IProgress<string>
{
    /// <summary>
    /// How much to see before deciding about the label. Long enough that one is always complete
    /// along with some of what follows — "Ava: " is five characters — and short enough not to be a
    /// visible stall.
    /// </summary>
    private const int Window = 40;

    /// <summary>
    /// Longest a held asterisk span may grow before it is decided not to be a gesture. A gesture is
    /// a phrase; anything longer is prose that happened to contain an asterisk, and holding it back
    /// would stall the stream for no reason.
    /// </summary>
    private const int LongestSpan = 200;

    private readonly IProgress<string> _inner;
    private readonly string? _speaker;

    private StringBuilder? _opening = new();
    private readonly StringBuilder _span = new();
    private bool _inSpan;

    public SelfLabelSink(IProgress<string> inner, string? speaker)
    {
        _inner = inner;
        _speaker = speaker;
    }

    public void Report(string value)
    {
        if (_opening is not null)
        {
            _opening.Append(value);
            if (_opening.Length < Window)
                return;

            var opening = PromptEchoFilter.TrimSelfLabel(_opening.ToString(), _speaker);
            _opening = null;
            Push(opening);
            return;
        }

        Push(value);
    }

    /// <summary>
    /// Emits whatever is still held back. Must be called when the round ends, or a reply shorter
    /// than the window — or one ending mid-span — would never be delivered at all.
    /// </summary>
    public void Flush()
    {
        if (_opening is not null)
        {
            var opening = PromptEchoFilter.TrimSelfLabel(_opening.ToString(), _speaker);
            _opening = null;
            Push(opening);
        }

        if (_span.Length > 0)
        {
            // Never closed, so it was never a gesture. Her words go out unchanged.
            _inner.Report(_span.ToString());
            _span.Clear();
            _inSpan = false;
        }
    }

    private void Push(string text)
    {
        if (text.Length == 0)
            return;

        var safe = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (_inSpan)
            {
                _span.Append(ch);

                if (ch == '*')
                {
                    var span = _span.ToString();
                    _span.Clear();
                    _inSpan = false;
                    if (!PromptEchoFilter.IsNarratedGesture(span))
                        safe.Append(span);
                }
                else if (ch == '\n' || _span.Length >= LongestSpan)
                {
                    safe.Append(_span);
                    _span.Clear();
                    _inSpan = false;
                }

                continue;
            }

            if (ch == '*')
            {
                _inSpan = true;
                _span.Append(ch);
                continue;
            }

            safe.Append(ch);
        }

        if (safe.Length > 0)
            _inner.Report(safe.ToString());
    }
}
