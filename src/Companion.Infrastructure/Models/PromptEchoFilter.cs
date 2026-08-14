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

    /// <summary>
    /// A line that opens somebody else's turn: a bare role name, a "User:"/"Assistant:" prefix, or
    /// one of the packet's speaker labels.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(user|assistant|system)\b[ \t]*:?[ \t]*$|^[ \t]*(User|Assistant|System)[ \t]*:|^[ \t]*\[(USER|COMPANION|SYSTEM)\]",
        RegexOptions.Multiline)]
    private static partial Regex RoleMarker();

    /// <summary>A packet speaker label at the very start, e.g. "[COMPANION - Ava]".</summary>
    [GeneratedRegex(@"^\[(USER|COMPANION|SYSTEM)[^\]\n]*\][ \t]*:?[ \t]*")]
    private static partial Regex LeadingBracketLabel();

    /// <summary>
    /// Removes her own name from the front of her own reply.
    ///
    /// The packet labels the transcript by speaker — "[COMPANION - Ava]" — and a model shown that
    /// shape adopts it, opening with "Ava: ". Left alone it compounds: the prefixed reply is stored,
    /// fed back as the previous turn, and now demonstrates the format directly under the label, so
    /// the next reply does it too. Observed on every turn of a fresh conversation.
    ///
    /// Only ever removes a label for the speaker herself, or the generic role words. A prefix that
    /// is not her name is left alone — "Note:" and "Update:" are things she might legitimately
    /// write, and trimming her actual words is worse than leaving an artefact visible.
    /// </summary>
    public static string TrimSelfLabel(string reply, string? speaker)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return reply;

        // A model that opened with "[COMPANION - Ava] Ava:" needs more than one pass, but this is
        // bounded: it is stripping a prefix, not searching.
        var text = reply;
        for (var pass = 0; pass < 3; pass++)
        {
            var stripped = StripOneLabel(text, speaker);
            if (stripped.Length == text.Length)
                break;
            text = stripped;
        }

        return text;
    }

    private static string StripOneLabel(string text, string? speaker)
    {
        var start = text.TrimStart();

        var bracket = LeadingBracketLabel().Match(start);
        if (bracket.Success)
        {
            var withoutBracket = start[bracket.Length..].TrimStart();
            return withoutBracket.Length > 0 ? withoutBracket : text;
        }

        // A speaker label is a short name followed by a colon. Anything longer is a sentence.
        var colon = start.IndexOf(':');
        if (colon > 0 && colon <= 40 && IsSelf(start[..colon].Trim(), speaker))
        {
            var afterColon = start[(colon + 1)..].TrimStart();
            // Never return nothing: a reply that is only a label is strange, but it is hers.
            return afterColon.Length > 0 ? afterColon : text;
        }

        return StripBareName(start, text, speaker);
    }

    /// <summary>
    /// The same label with the punctuation missing — "Ava Hi again!", seen live after the colon
    /// form was fixed. A roleplay fine-tune is trained on "Name:" dialogue and will reach for the
    /// name whether or not it remembers the colon.
    ///
    /// Guarded by what comes next: her name followed by a capital letter is a label on a fresh
    /// sentence, while her name followed by a lower-case word is a sentence that happens to start
    /// with it ("Ava is what my mother chose"), and that is left alone. Only whitespace and dashes
    /// count as the separator — a comma is far more likely to be someone being addressed.
    /// </summary>
    private static string StripBareName(string start, string original, string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return original;

        var name = speaker.Trim();
        if (start.Length <= name.Length || !start.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return original;

        var after = start[name.Length..];
        var gap = 0;
        while (gap < after.Length && (char.IsWhiteSpace(after[gap]) || after[gap] is '-' or '–' or '—'))
            gap++;

        if (gap == 0)
            return original; // "Avalanche" is not "Ava"

        var rest = after[gap..];
        return rest.Length > 0 && char.IsUpper(rest[0]) ? rest : original;
    }

    private static bool IsSelf(string candidate, string? speaker)
    {
        if (candidate.Length == 0)
            return false;
        if (!string.IsNullOrWhiteSpace(speaker)
            && candidate.Equals(speaker.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        return candidate.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("companion", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cuts a reply at the point the model started writing somebody else's turn.
    ///
    /// This is the severe form of the same failure. Left alone, the model does not stop at the end
    /// of her turn: it writes the user's next line, then its own reply, and continues — turning one
    /// message into an invented dialogue that escalates with no input at all. The fabrication is
    /// then stored as her reply, fed back as context, and can reach extraction as though the user
    /// had said it.
    ///
    /// Stop sequences are the real defence, since they prevent the text existing. This catches a
    /// provider that ignores them.
    /// </summary>
    public static string TrimFabricatedTurns(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return reply;

        var lines = reply.Replace("\r\n", "\n").Split('\n');

        for (var i = 1; i < lines.Length; i++)
        {
            if (!RoleMarker().IsMatch(lines[i]))
                continue;

            var before = string.Join("\n", lines.Take(i)).TrimEnd();
            // Never return nothing: a reply that is only a role marker is strange but hers.
            return before.Length > 0 ? before : reply;
        }

        return reply;
    }

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
