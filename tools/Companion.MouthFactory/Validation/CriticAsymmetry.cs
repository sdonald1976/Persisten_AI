using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>One content variant in the matched-pair audit. Not a rating — a register label.</summary>
public enum ContentVariant { Neutral, Romantic, Explicit, Profane, DarkFiction }

/// <summary>
/// A matched set: the SAME structural task rendered in each register, with identical structural
/// fidelity. Any difference in critic rejection between them is the critic's prejudice, not the
/// rows' quality, because by construction the rows are equally correct.
/// </summary>
public sealed record MatchedPair
{
    public required string Id { get; init; }
    public required IReadOnlyDictionary<ContentVariant, string> Targets { get; init; }
    public required ScenarioTruth Scenario { get; init; }
}

public sealed record VariantResult(ContentVariant Variant, int Judged, int Rejected)
{
    public double RejectionRate => Judged == 0 ? 0 : (double)Rejected / Judged;
}

public sealed record AsymmetryReport
{
    public required IReadOnlyList<VariantResult> Variants { get; init; }
    public required double Ceiling { get; init; }

    public VariantResult Baseline => Variants.First(v => v.Variant == ContentVariant.Neutral);

    /// <summary>Each variant's rejection rate minus the neutral baseline's.</summary>
    public IReadOnlyDictionary<ContentVariant, double> Deltas =>
        Variants.ToDictionary(v => v.Variant, v => v.RejectionRate - Baseline.RejectionRate);

    public double WorstDelta => Deltas.Values.DefaultIfEmpty(0).Max();

    /// <summary>
    /// The critic may be used only if no register is rejected materially more than neutral. A
    /// critic that fails this is recalibrated or replaced — the MATERIAL is never removed.
    /// </summary>
    public bool CriticAcceptable => WorstDelta <= Ceiling;

    public IReadOnlyList<ContentVariant> OffendingVariants =>
        Deltas.Where(kv => kv.Value > Ceiling).Select(kv => kv.Key).ToList();
}

/// <summary>
/// The gate every LLM critic must pass before its opinion counts.
///
/// The failure this prevents is specific and very likely: a general-purpose instruct model asked
/// "is this reply good?" rejects sexual, profane and dark material at a higher rate than neutral
/// material regardless of whether it satisfied the plan. Wire such a critic into an acceptance
/// pipeline and it quietly strips A6 and A7 out of the corpus — the mouth then cannot do the
/// things it was specifically commissioned to do, and the metrics all look fine because the rows
/// were "low quality".
///
/// So: identical structural fidelity across five registers, measure rejection, compare to
/// neutral. Over the ceiling and the critic is replaced or recalibrated. The material stays.
/// </summary>
public static class CriticAsymmetry
{
    /// <summary>
    /// Declared ceiling on excess rejection versus neutral. 0.05 = five percentage points, which
    /// is a real but tolerable amount of noise at pilot sample sizes.
    /// </summary>
    public const double DefaultCeiling = 0.05;

    public static async Task<AsymmetryReport> AuditAsync(
        IReadOnlyList<MatchedPair> pairs,
        Func<ScenarioTruth, string, CancellationToken, Task<bool>> criticRejects,
        double ceiling = DefaultCeiling,
        CancellationToken ct = default)
    {
        var counts = Enum.GetValues<ContentVariant>()
            .ToDictionary(v => v, _ => (Judged: 0, Rejected: 0));

        foreach (var pair in pairs)
        {
            foreach (var (variant, target) in pair.Targets)
            {
                var rejected = await criticRejects(pair.Scenario, target, ct);
                var current = counts[variant];
                counts[variant] = (current.Judged + 1, current.Rejected + (rejected ? 1 : 0));
            }
        }

        return new AsymmetryReport
        {
            Ceiling = ceiling,
            Variants = counts
                .Select(kv => new VariantResult(kv.Key, kv.Value.Judged, kv.Value.Rejected))
                .OrderBy(v => v.Variant)
                .ToList(),
        };
    }
}
