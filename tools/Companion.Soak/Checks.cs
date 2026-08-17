using System.Text.RegularExpressions;

namespace Companion.Soak;

/// <summary>A thing that went wrong, in terms a person can act on.</summary>
public sealed record Fault(string Check, string Detail, string Excerpt);

/// <summary>
/// What must be true of every reply, whatever the conversation.
///
/// Each of these guards a fault that actually happened and reached a real conversation — none is
/// hypothetical. They assert properties rather than text, because the model is stochastic and an
/// exact-output assertion would either be rewritten every run or quietly weakened until it passed.
///
/// The unit suite cannot cover any of this. Every one of these failures lived in a seam — a role
/// pointed at a model too small to do its job, a prompt that overflowed a window nothing knew the
/// size of, a filter that ran everywhere except the path a person actually talks through — and the
/// components on both sides of each seam were individually correct and individually tested.
/// </summary>
public static class Checks
{
    /// <summary>She introduces herself by name at the start of her own reply.</summary>
    private static readonly Regex SelfLabel =
        new(@"^\s*(ava|assistant|companion)\s*[:—-]|^\s*ava\s+[A-Z]|^\s*\[(user|companion|system)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Narrated gestures — *chuckles warmly*, *nods understandingly*.</summary>
    private static readonly Regex StageDirection =
        new(@"\*[a-z][^*\n]{3,}\*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The packet's own provenance vocabulary, used as inline annotation.</summary>
    private static readonly Regex ProvenanceMarker =
        new(@"\((direct|inferred|outdated)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A heading or speaker label out of the context packet.</summary>
    private static readonly Regex PacketStructure =
        new(@"(^|\n)\s*(##\s|\[USER\]|\[COMPANION)", RegexOptions.Compiled);

    /// <summary>
    /// Talking about the person she is talking to, in the third person. It arrived as a side effect
    /// of forbidding her to describe their projects in the first person: told not to say "I've been
    /// mixing the compost", the model kept the invention and changed the grammar, producing "the
    /// user has completed two beds and begun filling them". Whatever else is true of a reply, the
    /// listener is "you".
    /// </summary>
    private static readonly Regex ThirdPersonUser =
        new(@"\bthe user\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Everything that must hold for a single reply, given what came before it in the conversation.
    /// </summary>
    public static IEnumerable<Fault> ForTurn(Turn turn, IReadOnlyList<Turn> earlier, int promptBudget)
    {
        var reply = turn.Reply;

        if (string.IsNullOrWhiteSpace(reply))
        {
            yield return new Fault("empty-reply", "she returned nothing at all", "");
            yield break;
        }

        if (SelfLabel.IsMatch(reply))
            yield return new Fault("self-label", "opened by naming herself", Head(reply));

        if (StageDirection.Match(reply) is { Success: true } stage)
            yield return new Fault("stage-direction", "narrated a gesture she has no body for", stage.Value);

        if (ProvenanceMarker.Match(reply) is { Success: true } marker)
            yield return new Fault("provenance-marker", "annotated her speech with packet vocabulary", marker.Value);

        if (PacketStructure.IsMatch(reply))
            yield return new Fault("packet-echo", "reproduced the shape of her own context packet", Head(reply));

        if (ThirdPersonUser.IsMatch(reply))
            yield return new Fault("third-person-user", "referred to the person she's talking to as \"the user\"", Head(reply));

        // She reproduced an earlier turn rather than answering this one.
        foreach (var prior in earlier)
        {
            var overlap = LeadingOverlap(reply, prior.Reply);
            if (overlap >= 120)
            {
                yield return new Fault(
                    "echoed-turn", $"began with {overlap} characters of an earlier reply, verbatim", Head(reply));
                break;
            }
        }

        // The prompt has to fit the window, or the server silently discards the top of it — which is
        // where identity lives.
        if (promptBudget > 0 && turn.PacketTokens > promptBudget)
        {
            yield return new Fault(
                "prompt-over-budget",
                $"packet was ~{turn.PacketTokens} tokens against a {promptBudget} budget",
                "");
        }
    }

    /// <summary>
    /// Reply shape for a short, casual message. A warning rather than a fault: this is a judgement
    /// about voice, and a model will not honour it every time.
    /// </summary>
    public static IEnumerable<Fault> ForRegister(Turn turn)
    {
        if (turn.Sent.Trim().Length > 50)
            yield break;

        var questions = turn.Reply.Count(c => c == '?');
        if (questions > 1)
            yield return new Fault("interrogation", $"{questions} questions in reply to a short message", Head(turn.Reply));

        if (turn.Reply.Length > 700)
            yield return new Fault("over-long", $"{turn.Reply.Length} characters in reply to {turn.Sent.Trim().Length}", "");
    }

    /// <summary>How much of <paramref name="prior"/> the start of <paramref name="reply"/> repeats.</summary>
    private static int LeadingOverlap(string reply, string prior)
    {
        int i = 0, j = 0, matched = 0;
        while (i < reply.Length && j < prior.Length)
        {
            if (char.IsWhiteSpace(reply[i])) { i++; continue; }
            if (char.IsWhiteSpace(prior[j])) { j++; continue; }
            if (reply[i] != prior[j]) break;
            i++; j++; matched++;
        }
        return matched;
    }

    private static string Head(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 90 ? flat : flat[..90] + "…";
    }
}
