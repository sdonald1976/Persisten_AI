using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Deterministic relevance features computed per turn for OBSERVATION — nothing consumes them
/// yet. The question they exist to answer: "retrieval returned records" versus "retrieval
/// returned evidence that actually supports answering this turn." The weighted score cannot
/// answer it (recency and importance rank well but say nothing about aboutness) and the raw
/// topical score cannot either (question scaffolding inflates overlap between any two
/// activity-shaped sentences — measured live 2026-08-20). Whether focal containment CAN
/// answer it is what the shadow corpus is for. This must never grow into a second retrieval
/// engine: one feature, characterized before use.
/// </summary>
public static partial class RelevanceSignals
{
    private const int MaxCoveredByChars = 120;

    /// <summary>
    /// The focal terms of the user's message (content words with question scaffolding
    /// stripped) and whether any retrieved memory contains one. Null when the message has no
    /// focal terms to check — coverage of nothing is not evidence of anything.
    /// </summary>
    public static FocalCoverage? Focal(string userMessage, IReadOnlyList<RetrievalResult> retrieved)
    {
        var terms = Word().Matches(userMessage)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 3 && !Scaffolding.Contains(w))
            .Distinct()
            .ToList();
        if (terms.Count == 0)
            return null;

        foreach (var result in retrieved)
        {
            foreach (var term in terms)
            {
                // Whole-word containment — "car" must not claim coverage from "carburetor".
                if (Regex.IsMatch(result.Memory.Content, $@"\b{Regex.Escape(term)}\b",
                        RegexOptions.IgnoreCase))
                {
                    var content = result.Memory.Content;
                    return new FocalCoverage(terms, Covered: true,
                        content.Length <= MaxCoveredByChars ? content : content[..MaxCoveredByChars]);
                }
            }
        }
        return new FocalCoverage(terms, Covered: false, null);
    }

    /// <summary>
    /// Words that shape a question without naming its subject — the contamination the live
    /// run measured. "How's my treehouse project going?" must reduce to "treehouse", not
    /// score on "project" and "going" against every activity memory in the store.
    /// </summary>
    private static readonly HashSet<string> Scaffolding = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "when", "where", "which", "whose", "there", "these", "those", "this", "that",
        "have", "been", "being", "were", "will", "would", "could", "should", "does", "doing",
        "done", "going", "coming", "along", "about", "with", "from", "into", "your", "yours",
        "mine", "their", "them", "they", "some", "something", "anything", "ever", "still",
        "just", "like", "really", "think", "know", "tell", "remind", "decide", "decided",
        "progress", "update", "updates", "project", "week", "month", "today", "tomorrow",
        "yesterday", "time", "name", "thing", "things", "stuff", "much", "many", "more",
        "how's",
    };

    [GeneratedRegex(@"[a-zA-Z']+")]
    private static partial Regex Word();
}
