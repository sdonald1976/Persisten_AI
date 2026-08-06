using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>What to do with a user message that follows a pending clarification.</summary>
public enum ClarificationDecisionKind
{
    /// <summary>The message picked a candidate.</summary>
    Resolved,

    /// <summary>The user backed out ("never mind").</summary>
    Cancelled,

    /// <summary>The reply still doesn't clearly select one candidate — ask again.</summary>
    StillAmbiguous,
}

public sealed record ClarificationDecision(ClarificationDecisionKind Kind, Guid? ProjectId, string? Note)
{
    public static readonly ClarificationDecision Ambiguous = new(ClarificationDecisionKind.StillAmbiguous, null, null);
    public static readonly ClarificationDecision Cancel = new(ClarificationDecisionKind.Cancelled, null, "user cancelled");
    public static ClarificationDecision Pick(ClarificationCandidate c, string how) =>
        new(ClarificationDecisionKind.Resolved, c.ProjectId, $"matched \"{c.Name}\" {how}");
}

/// <summary>
/// Deterministic, conservative resolution of a clarification reply against the candidates that
/// were offered. Application code decides — never the chat model — so the same reply always
/// resolves the same way. It picks a candidate only on a clear signal (explicit cancellation,
/// an ordinal like "the first one", "the other one" with two choices, or an unambiguous name
/// match); anything vague stays <see cref="ClarificationDecisionKind.StillAmbiguous"/> so the
/// system asks again rather than guessing. Vague phrases never become durable aliases.
/// </summary>
public static class ClarificationResolver
{
    private static readonly Regex CancelRx = new(
        @"\b(never ?mind|nvm|forget it|forget about it|cancel|drop it|skip it|stop|no thanks|" +
        @"neither|none( of them)?|not (that|those)|don'?t worry)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Ordinal words/numerals mapped to a zero-based index. "last" is handled separately.
    private static readonly (Regex Rx, int Index)[] Ordinals =
    {
        (new Regex(@"\b(first|1st|number one|option one|#?1)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 0),
        (new Regex(@"\b(second|2nd|number two|option two|#?2)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 1),
        (new Regex(@"\b(third|3rd|number three|option three|#?3)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 2),
    };

    private static readonly Regex LastRx = new(@"\b(last|latter|second one)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OtherRx = new(@"\b(the )?other( one)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ClarificationDecision Resolve(string reply, IReadOnlyList<ClarificationCandidate> candidates)
    {
        if (candidates.Count == 0)
            return ClarificationDecision.Ambiguous;

        var text = (reply ?? "").Trim();
        if (text.Length == 0)
            return ClarificationDecision.Ambiguous;

        // 1. Explicit cancellation always wins.
        if (CancelRx.IsMatch(text))
            return ClarificationDecision.Cancel;

        // 2. Name match — the strongest and most common signal ("the buoy one").
        var byName = MatchByName(text, candidates);
        if (byName is not null)
            return byName;

        // 3. Ordinal ("the first one", "option 2").
        foreach (var (rx, index) in Ordinals)
        {
            if (rx.IsMatch(text) && index < candidates.Count)
                return ClarificationDecision.Pick(candidates[index], "by ordinal");
        }
        if (LastRx.IsMatch(text))
            return ClarificationDecision.Pick(candidates[^1], "by ordinal (last)");

        // 4. "the other one" — only unambiguous when there are exactly two choices.
        if (OtherRx.IsMatch(text) && candidates.Count == 2)
            return ClarificationDecision.Pick(candidates[^1], "by elimination");

        // 5. Nothing clear → ask again.
        return ClarificationDecision.Ambiguous;
    }

    /// <summary>Pick a candidate whose name clearly matches the reply, requiring a decisive lead.</summary>
    private static ClarificationDecision? MatchByName(string text, IReadOnlyList<ClarificationCandidate> candidates)
    {
        var scored = candidates
            .Select(c => (c, score: NameScore(text, c.Name)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count == 0)
            return null;

        var top = scored[0];
        var second = scored.Count > 1 ? scored[1].score : 0.0;

        // Decisive only when the leader clearly dominates (or is the sole match).
        if (second == 0 || top.score >= second * 1.5)
            return ClarificationDecision.Pick(top.c, "by name");

        return null; // two names match comparably → still ambiguous
    }

    /// <summary>
    /// Overlap of the reply's words with the candidate name's distinctive words. Common filler
    /// ("the", "one", "project") is ignored so "the buoy one" keys on "buoy".
    /// </summary>
    private static double NameScore(string reply, string name)
    {
        var replyTokens = Tokenize(reply);
        var nameTokens = Tokenize(name).Where(t => !Filler.Contains(t)).ToHashSet();
        if (nameTokens.Count == 0)
            return 0;
        return nameTokens.Count(replyTokens.Contains);
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "one", "ones", "a", "an", "project", "thing", "mean", "yeah", "yes", "no", "my", "that",
    };

    private static HashSet<string> Tokenize(string text) => Regex
        .Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
        .Select(m => m.Value)
        .ToHashSet();
}
