using System.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Strips a model's machinery out of its output, leaving only the actual reply. Two kinds:
///
/// <list type="bullet">
/// <item>Chain-of-thought reasoning (<c>&lt;think&gt;…&lt;/think&gt;</c>, also <c>&lt;thinking&gt;</c>).
/// Reasoning is ephemeral scratch: it must never be shown, stored, or — critically — fed back into
/// the next turn's context, because a small model that sees its own prior reasoning and replies
/// replayed verbatim collapses into repeating itself.</item>
/// <item>Special-token spans (<c>&lt;|…|&gt;</c>). A conversational fine-tune that has been shown a
/// tool-call protocol will sometimes imitate the shape of it in prose — an observed reply ended
/// <c>&lt;|"memory.confirmed".code="ok", …|&gt;</c> — and Llama-family models use this delimiter for
/// their own control tokens. Either way it is protocol leaking into the conversation, and the user
/// should never see it.</item>
/// </list>
///
/// Works incrementally so it can filter a token stream: a delimiter may be split across chunks, so
/// a possible partial one at a chunk boundary is held back until the next chunk resolves it.
/// </summary>
internal sealed class ReasoningFilter
{
    private const string Open = "<think";   // matches <think> and <thinking>
    private const string ClosePrefix = "</think";
    private const string OpenSpecial = "<|";
    private const string CloseSpecial = "|>";

    private enum Mode { Visible, Reasoning, Special }

    private Mode _mode = Mode.Visible;
    private string _carry = "";

    /// <summary>Feed a chunk; returns the visible text to emit now (may be empty).</summary>
    public string Feed(string text)
    {
        var buffer = _carry + text;
        _carry = "";
        var output = new StringBuilder();

        while (buffer.Length > 0)
        {
            if (_mode == Mode.Visible)
            {
                var thinkAt = buffer.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
                var specialAt = buffer.IndexOf(OpenSpecial, StringComparison.Ordinal);

                if (thinkAt < 0 && specialAt < 0)
                {
                    // Nothing opens here. Emit everything except a trailing run that could still
                    // grow into either opener once the next chunk arrives.
                    var hold = Math.Max(PartialSuffix(buffer, Open), PartialSuffix(buffer, OpenSpecial));
                    output.Append(buffer, 0, buffer.Length - hold);
                    if (hold > 0) _carry = buffer[^hold..];
                    break;
                }

                var specialFirst = thinkAt < 0 || (specialAt >= 0 && specialAt < thinkAt);
                var idx = specialFirst ? specialAt : thinkAt;
                output.Append(buffer, 0, idx);

                if (specialFirst)
                {
                    buffer = buffer[(idx + OpenSpecial.Length)..];
                    _mode = Mode.Special;
                    continue;
                }

                var gt = buffer.IndexOf('>', idx);
                if (gt < 0) { _carry = buffer[idx..]; break; } // incomplete open tag; wait
                buffer = buffer[(gt + 1)..];
                _mode = Mode.Reasoning;
                continue;
            }

            if (_mode == Mode.Special)
            {
                var closeAt = buffer.IndexOf(CloseSpecial, StringComparison.Ordinal);
                if (closeAt >= 0)
                {
                    buffer = buffer[(closeAt + CloseSpecial.Length)..];
                    _mode = Mode.Visible;
                    continue;
                }

                var hold = PartialSuffix(buffer, CloseSpecial);
                _carry = hold > 0 ? buffer[^hold..] : "";
                break;
            }

            var close = buffer.IndexOf(ClosePrefix, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                var gt = buffer.IndexOf('>', close);
                if (gt < 0) { _carry = buffer[close..]; break; } // incomplete close tag; wait
                buffer = buffer[(gt + 1)..];
                _mode = Mode.Visible;
                continue;
            }

            // Still inside reasoning; hold only a possible partial close tag.
            var held = PartialSuffix(buffer, ClosePrefix);
            _carry = held > 0 ? buffer[^held..] : "";
            break;
        }

        return output.ToString();
    }

    /// <summary>Emit any safely-buffered trailing text. An unclosed span is dropped.</summary>
    public string Flush()
    {
        // Inside an unterminated <think> or <| → it never closed; drop it entirely. Otherwise the
        // carry is a held partial-opener that never completed → also drop.
        _carry = "";
        _mode = Mode.Visible;
        return "";
    }

    /// <summary>Strip reasoning from a complete (non-streamed) string.</summary>
    public static string StripAll(string text)
    {
        var f = new ReasoningFilter();
        return (f.Feed(text) + f.Flush()).Trim();
    }

    /// <summary>Length of the longest suffix of <paramref name="s"/> that is a proper prefix of <paramref name="tag"/>.</summary>
    private static int PartialSuffix(string s, string tag)
    {
        var max = Math.Min(s.Length, tag.Length - 1);
        for (var k = max; k > 0; k--)
            if (string.Compare(s, s.Length - k, tag, 0, k, StringComparison.OrdinalIgnoreCase) == 0)
                return k;
        return 0;
    }
}
