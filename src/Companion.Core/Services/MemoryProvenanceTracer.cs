using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Builds the per-turn <see cref="MemoryProvenance"/> records from artifacts the pipeline already
/// produced. A pure function: no I/O, no model calls, no mutation. Recorded post-turn, it cannot
/// affect the displayed reply or any cognitive state.
///
/// The whole point is CONSERVATIVE labelling. Two rules from Scott's constraint drive it:
///   1. A memory merely appearing in the prompt is NOT a positive relevance label.
///   2. A relevant memory going unexpressed is NOT a negative label when suppression, question
///      policy, brevity, exclusion, or a failed turn could have prevented expression.
/// Everything that is not clearly positive or clearly negative is <see cref="MemoryRelevanceLabel.Unknown"/>,
/// and Unknown is never a negative training example.
/// </summary>
public static class MemoryProvenanceTracer
{
    /// <summary>Everything the tracer needs, all already computed by the turn.</summary>
    public sealed record Inputs
    {
        public required Guid TurnId { get; init; }

        /// <summary>Retrieved memories that were SELECTED (survived score/floor and TopK).</summary>
        public required IReadOnlyList<(Guid Id, double Score, int Rank)> Selected { get; init; }

        /// <summary>Retrieved memories that were EXCLUDED, with the mechanical reason.</summary>
        public required IReadOnlyList<(Guid Id, MemoryExclusionReason Reason)> Excluded { get; init; }

        /// <summary>Memory ids that survived into the context packet (post-trim).</summary>
        public required IReadOnlyCollection<Guid> RetainedInPacket { get; init; }

        /// <summary>Plan items that carry a memory id, with the item id and its expression policy.</summary>
        public required IReadOnlyList<(Guid MemoryId, string PlanItemId, string Policy)> PlanItems { get; init; }

        /// <summary>The displayed reply, or null when the turn produced none (failed/aborted).</summary>
        public string? DisplayedReply { get; init; }

        /// <summary>The memory's text, by id — used ONLY for the lexical expression proxy. Never stored.</summary>
        public required IReadOnlyDictionary<Guid, string> Texts { get; init; }

        /// <summary>True when the turn did not produce a displayed reply.</summary>
        public bool TurnFailed { get; init; }
    }

    private static readonly HashSet<string> SuppressivePolicies =
        new(StringComparer.Ordinal) { "must_not_express", "background_only" };

    public static IReadOnlyList<MemoryProvenance> Build(Inputs input)
    {
        var selected = input.Selected.ToDictionary(x => x.Id, x => (x.Score, x.Rank));
        var excluded = input.Excluded
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Reason);
        var retained = input.RetainedInPacket.ToHashSet();
        var planByMemory = input.PlanItems
            .GroupBy(x => x.MemoryId)
            .ToDictionary(g => g.Key, g => g.First());

        var allIds = selected.Keys
            .Concat(excluded.Keys)
            .Concat(retained)
            .Concat(planByMemory.Keys)
            .Distinct()
            .ToList();

        var records = new List<MemoryProvenance>(allIds.Count);
        foreach (var id in allIds)
        {
            var isSelected = selected.TryGetValue(id, out var sr);
            var inPacket = retained.Contains(id);
            var hasItem = planByMemory.TryGetValue(id, out var item);
            var policy = hasItem ? item.Policy : null;
            var suppressed = policy is not null && SuppressivePolicies.Contains(policy);

            // "Available to the Mouth" means available FOR EXPRESSION: visible in the packet or
            // carried by a plan item, AND not suppressed. A must_not_express/background_only
            // memory is visible to the Mouth but carried precisely so it is NOT expressed, so it
            // is not available in the sense that matters for a relevance label.
            var availableToMouth = (inPacket || hasItem) && !suppressed;

            // Exclusion reason, mechanical. Priority: turn failure, then plan suppression, then
            // the retrieval-stage reason. A memory can be selected yet suppressed by the plan.
            var exclusion = MemoryExclusionReason.None;
            if (input.TurnFailed)
                exclusion = MemoryExclusionReason.TurnFailedOrAborted;
            else if (suppressed)
                exclusion = MemoryExclusionReason.SuppressedByPlan;
            else if (excluded.TryGetValue(id, out var er))
                exclusion = er;
            else if (isSelected && !inPacket)
                exclusion = MemoryExclusionReason.TrimmedFromPacket;

            // Expression: a lexical proxy over the reply, only when a reply exists and the memory
            // was actually available. Never "expressed" for a suppressed or failed-turn memory.
            var expressed = ExpressionEvidence.NotEvaluated;
            if (!input.TurnFailed && input.DisplayedReply is { } reply && availableToMouth
                && input.Texts.TryGetValue(id, out var text))
                expressed = LexicallyOverlaps(text, reply)
                    ? ExpressionEvidence.LikelyExpressed
                    : ExpressionEvidence.NotObservablyExpressed;

            var (label, basis) = Label(
                isSelected, inPacket, hasItem, policy, suppressed, availableToMouth,
                exclusion, expressed, input.TurnFailed);

            records.Add(new MemoryProvenance
            {
                TurnId = input.TurnId,
                MemoryId = id,
                Retrieved = isSelected || excluded.ContainsKey(id),
                RerankerScore = isSelected ? sr.Score : null,
                RerankerRank = isSelected ? sr.Rank : null,
                RetainedInPacket = inPacket,
                ReferencedByPlanItemId = hasItem ? item.PlanItemId : null,
                PlanItemPolicy = policy,
                AvailableToMouth = availableToMouth,
                ExclusionReason = exclusion,
                Expressed = expressed,
                Label = label,
                LabelBasis = basis,
            });
        }

        return records;
    }

    /// <summary>
    /// The conservative label. Positive needs an authorizing plan reference AND lexical
    /// expression. Negative needs the memory to have been genuinely free to surface and still
    /// absent - nothing suppressing it, excluding it, or failing the turn. Everything else is
    /// Unknown, and Unknown is never trained as a negative.
    /// </summary>
    private static (MemoryRelevanceLabel, string) Label(
        bool selected, bool inPacket, bool hasItem, string? policy, bool suppressed,
        bool availableToMouth, MemoryExclusionReason exclusion, ExpressionEvidence expressed,
        bool turnFailed)
    {
        if (turnFailed)
            return (MemoryRelevanceLabel.Unknown, "turn produced no reply");

        // Positive: an authorizing (expressible) plan item carried it AND it surfaced lexically.
        var authorizing = hasItem && !suppressed
            && policy is "must_express" or "may_express" or "admit_unknown";
        if (authorizing && expressed == ExpressionEvidence.LikelyExpressed)
            return (MemoryRelevanceLabel.Positive,
                $"referenced by {policy} plan item and lexically expressed");

        // A memory the plan deliberately withheld says nothing about relevance.
        if (suppressed)
            return (MemoryRelevanceLabel.Unknown,
                $"withheld by plan ({policy}); relevance not decidable");

        // Excluded before it ever had a chance to be used - not a relevance signal.
        if (exclusion is MemoryExclusionReason.BelowRelevanceFloor
            or MemoryExclusionReason.NotSelected)
            return (MemoryRelevanceLabel.Unknown, $"excluded upstream ({exclusion})");

        // Negative, narrowly: it was available to the Mouth, no plan constraint touched it, and
        // it still did not observably surface. Even here, "not observably expressed" is a proxy,
        // so this is the strongest Negative the mechanics honestly support - and only when the
        // memory was NOT the subject of any plan item (a referenced-but-unexpressed item is the
        // Mouth ignoring the plan, a different failure, left Unknown for review).
        if (availableToMouth && !hasItem
            && expressed == ExpressionEvidence.NotObservablyExpressed)
            return (MemoryRelevanceLabel.Negative,
                "available and unconstrained, not observably expressed");

        // Referenced by an expressible item but not observably expressed: the Mouth may have
        // ignored it, or expressed it implicitly. Not decidable mechanically.
        if (authorizing && expressed == ExpressionEvidence.NotObservablyExpressed)
            return (MemoryRelevanceLabel.Unknown,
                "referenced but not observably expressed; needs review");

        return (MemoryRelevanceLabel.Unknown, "no decisive mechanical evidence");
    }

    /// <summary>
    /// A deliberately conservative lexical proxy: at least two shared distinctive content words
    /// (length ≥ 5, not a stopword) OR one shared long token (≥ 8). It is a PROXY - paraphrase
    /// without shared words reads as NotObservablyExpressed, which is why that is never alone a
    /// negative label for a plan-referenced memory.
    /// </summary>
    private static bool LexicallyOverlaps(string memoryText, string reply)
    {
        var replyWords = Distinctive(reply);
        var shared = Distinctive(memoryText).Where(replyWords.Contains).ToList();
        return shared.Count >= 2 || shared.Any(w => w.Length >= 8);
    }

    private static HashSet<string> Distinctive(string text)
        => System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z'-]*")
            .Select(m => m.Value)
            .Where(w => w.Length >= 5 && !Stop.Contains(w))
            .ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "along", "being", "between", "could", "doesn't", "during",
        "every", "might", "needs", "other", "should", "since", "still", "their", "there",
        "these", "thing", "things", "those", "today", "tonight", "under", "until", "wants",
        "where", "which", "while", "would", "yours", "before", "really", "around", "another",
    };
}
