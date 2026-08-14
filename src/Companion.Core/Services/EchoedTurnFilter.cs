using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Removes a previous reply that she reproduced verbatim before getting to the new one.
///
/// Observed in a real conversation: asked "can you remember that I have a dog named Precious?", she
/// emitted all 679 characters of her opening reply from three turns earlier — word for word, down
/// to its closing question — then a blank line, then the actual answer. Her own transcript is in
/// the prompt, and a model shown a document will sometimes continue it rather than respond to it.
///
/// None of the existing defences caught it. Stop sequences and <c>TrimFabricatedTurns</c> key on a
/// role marker, and there is none — she copied the text alone. <c>PromptEchoFilter.Trim</c> only
/// ever removes a block at the END. This is the same failure arriving in the one shape nothing was
/// watching: bare, and at the front.
///
/// Deliberately narrow, for the same reason the other filters are. It strips only what is
/// effectively a whole earlier turn repeated at the very start, and never the last thing she says —
/// echoing a phrase is style, echoing an entire turn is a fault.
/// </summary>
public static class EchoedTurnFilter
{
    /// <summary>
    /// Shortest run worth calling an echo. Below this, matching openings are ordinary voice — she
    /// greets people similarly, and "It's good to hear from you" recurring is not a defect.
    /// </summary>
    public const int MinEchoChars = 120;

    /// <summary>
    /// How much of the earlier turn must be reproduced. Requiring nearly all of it separates
    /// "she repeated that turn" from "she opened the same way", which is the difference between a
    /// bug and a habit.
    /// </summary>
    private const double MustCoverPriorFraction = 0.8;

    /// <summary>
    /// The reply with any leading verbatim repeat of an earlier turn removed. Returns the reply
    /// unchanged when there is nothing to strip, and never returns empty — a reply that is nothing
    /// but an echo is a failure worth seeing rather than hiding.
    /// </summary>
    public static string Strip(string reply, IReadOnlyList<Message> recent)
    {
        if (string.IsNullOrWhiteSpace(reply) || recent.Count == 0)
            return reply;

        var priors = recent
            .Where(m => m.Role == MessageRole.Assistant)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (priors.Count == 0)
            return reply;

        var text = reply;

        // More than one can be stacked; bounded because each pass must shorten the text.
        for (var pass = 0; pass < 3; pass++)
        {
            var stripped = StripOne(text, priors);
            if (stripped.Length == text.Length)
                break;
            text = stripped;
        }

        return text;
    }

    private static string StripOne(string reply, IReadOnlyList<string> priors)
    {
        foreach (var prior in priors)
        {
            var (cut, matched) = LeadingOverlap(reply, prior);
            if (matched < MinEchoChars)
                continue;

            var priorSize = prior.Count(c => !char.IsWhiteSpace(c));
            if (priorSize == 0 || matched < priorSize * MustCoverPriorFraction)
                continue;

            var rest = reply[cut..].TrimStart();
            if (rest.Length > 0)
                return rest;
        }

        return reply;
    }

    /// <summary>
    /// How much of <paramref name="prior"/> the start of <paramref name="reply"/> reproduces,
    /// ignoring differences in whitespace, and where in the reply that run ends.
    ///
    /// Whitespace is ignored because the echo is not always byte-identical — the copy that
    /// prompted this had its paragraph breaks reflowed — and a repeat that survived on a change of
    /// line wrapping would be no less a repeat.
    /// </summary>
    private static (int Cut, int Matched) LeadingOverlap(string reply, string prior)
    {
        int i = 0, j = 0, matched = 0, cut = 0;

        while (i < reply.Length && j < prior.Length)
        {
            if (char.IsWhiteSpace(reply[i])) { i++; continue; }
            if (char.IsWhiteSpace(prior[j])) { j++; continue; }
            if (reply[i] != prior[j])
                break;

            i++;
            j++;
            matched++;
            cut = i;
        }

        return (cut, matched);
    }
}
