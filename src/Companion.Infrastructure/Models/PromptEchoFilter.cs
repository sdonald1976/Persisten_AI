using System.Text.RegularExpressions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Removes a trailing chunk of the context packet's own structure from a reply.
///
/// Her context arrives as a structured document, and a roleplay fine-tune shown a document will
/// sometimes continue it rather than answer. One real reply ended:
///
///     ---
///     Remembered items about the user so far:
///     - None (first conversation)
///
/// None of which appears in any prompt — she invented a packet-shaped section because the packet
/// taught her the shape. The prompt already forbids exactly this ("never repeat them back, never
/// print their headings") and was ignored, which is why the defence is mechanical rather than more
/// words: stop sequences prevent it being generated, and this catches a provider that ignores them.
///
/// Deliberately narrow. It only ever removes a block at the very END of a reply, introduced by a
/// horizontal rule or a markdown heading, whose content reads as structure rather than speech.
/// Trimming a companion's actual words would be far worse than leaving an artefact visible, so
/// every ambiguous case is left alone.
/// </summary>
internal static partial class PromptEchoFilter
{
    /// <summary>A line that is only dashes, equals or underscores — a horizontal rule.</summary>
    [GeneratedRegex(@"^[ \t]*([-=_])\1{2,}[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRule();

    /// <summary>A markdown heading at the start of a line.</summary>
    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex Heading();

    /// <summary>A line that introduces a list of things, e.g. "Remembered items about the user so far:".</summary>
    [GeneratedRegex(@"^[ \t]*[^\n]{0,120}:[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex LabelLine();

    /// <summary>A bullet.</summary>
    [GeneratedRegex(@"^[ \t]*[-*•][ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    public static string Trim(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return reply;

        var lines = reply.Replace("\r\n", "\n").Split('\n');

        // Walk backwards for the last marker that could open an echoed section. Only a marker with
        // real content before it counts: a reply that *starts* with one is her formatting a genuine
        // answer, not appending to the packet.
        for (var i = lines.Length - 1; i > 0; i--)
        {
            var line = lines[i];
            var isMarker = HorizontalRule().IsMatch(line) || Heading().IsMatch(line);
            if (!isMarker)
                continue;

            var after = string.Join("\n", lines.Skip(i + 1)).Trim();
            var before = string.Join("\n", lines.Take(i)).TrimEnd();

            if (before.Length == 0)
                return reply; // nothing but the marker — leave it alone

            // A heading with nothing after it is just a heading she wrote; only strip when the
            // marker is followed by something that reads as packet structure.
            if (!LooksLikeStructure(after))
                continue;

            return before;
        }

        return reply;
    }

    /// <summary>
    /// True when a block reads as a section of the packet rather than something she said: a label
    /// line ending in a colon, or nothing but bullets. Prose — anything with sentence-ending
    /// punctuation outside a bullet — is hers and is never touched.
    /// </summary>
    private static bool LooksLikeStructure(string block)
    {
        if (block.Length == 0)
            return false;

        var lines = block.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
            return false;

        var hasLabel = lines.Any(l => LabelLine().IsMatch(l));
        var allBullets = lines.All(l => Bullet().IsMatch(l));

        if (!hasLabel && !allBullets)
            return false;

        // A closing thought after the rule is still speech even if it contains a colon, so require
        // the non-bullet, non-label lines to be absent.
        return lines.All(l => LabelLine().IsMatch(l) || Bullet().IsMatch(l));
    }
}
