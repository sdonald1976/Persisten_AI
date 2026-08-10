using System.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Strips a model's chain-of-thought reasoning (<c>&lt;think&gt;…&lt;/think&gt;</c>, also
/// <c>&lt;thinking&gt;</c>) from its output, leaving only the actual reply. Reasoning is ephemeral
/// scratch: it must never be shown, stored, or — critically — fed back into the next turn's context,
/// because a small model that sees its own prior reasoning and replies replayed verbatim collapses
/// into repeating itself.
///
/// Works incrementally so it can filter a token stream: tags may be split across chunks, so a
/// possible partial tag at a chunk boundary is held back until the next chunk resolves it.
/// </summary>
internal sealed class ReasoningFilter
{
    private const string Open = "<think";   // matches <think> and <thinking>
    private const string Close = "</think>"; // closing form; we match up to the '>'
    private const string ClosePrefix = "</think";

    private bool _inThink;
    private string _carry = "";

    /// <summary>Feed a chunk; returns the visible text to emit now (may be empty).</summary>
    public string Feed(string text)
    {
        var buffer = _carry + text;
        _carry = "";
        var output = new StringBuilder();

        while (buffer.Length > 0)
        {
            if (!_inThink)
            {
                var idx = buffer.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    output.Append(buffer, 0, idx);
                    var gt = buffer.IndexOf('>', idx);
                    if (gt < 0) { _carry = buffer[idx..]; break; } // incomplete open tag; wait
                    buffer = buffer[(gt + 1)..];
                    _inThink = true;
                    continue;
                }

                // No open tag. Emit everything except a trailing run that could start one.
                var hold = PartialSuffix(buffer, Open);
                output.Append(buffer, 0, buffer.Length - hold);
                if (hold > 0) _carry = buffer[^hold..];
                break;
            }
            else
            {
                var idx = buffer.IndexOf(ClosePrefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var gt = buffer.IndexOf('>', idx);
                    if (gt < 0) { _carry = buffer[idx..]; break; } // incomplete close tag; wait
                    buffer = buffer[(gt + 1)..];
                    _inThink = false;
                    continue;
                }

                // Still inside reasoning; hold only a possible partial close tag.
                var hold = PartialSuffix(buffer, ClosePrefix);
                _carry = hold > 0 ? buffer[^hold..] : "";
                break;
            }
        }

        return output.ToString();
    }

    /// <summary>Emit any safely-buffered trailing text. Unclosed reasoning is dropped.</summary>
    public string Flush()
    {
        // Inside an unterminated <think> → the reasoning never closed; drop it entirely.
        // Otherwise the carry is a held partial-open-tag prefix that never completed → also drop.
        _carry = "";
        _inThink = false;
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
