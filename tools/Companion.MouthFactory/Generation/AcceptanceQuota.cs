using Companion.MouthFactory.Export;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>One quota bucket: what share it should hold, and what it holds.</summary>
public sealed record QuotaBucket(string Name, double Target, int Accepted, int Total)
{
    public double Share => Total == 0 ? 0 : Accepted / (double)Total;

    /// <summary>Percentage points below (positive) or above (negative) target.</summary>
    public double GapPoints => (Target - Share) * 100;

    /// <summary>How many more rows this bucket needs for the corpus to reach its share.</summary>
    public int Shortfall => Math.Max(0, (int)Math.Ceiling(Target * Total) - Accepted);
}

/// <summary>
/// The accepted-row distribution the corpus is required to deliver, and the machinery that makes
/// the run keep going until it does.
///
/// TWO RUNS ESTABLISHED WHY THIS HAS TO BIND. Question policies were assigned at scenario level in
/// production proportions both times, and both times the delivered corpus was something else:
/// 47.6% forbidden, then 54.6%, against 63.3%. Rejection is not uniform across policies —
/// unrequested-question falls almost entirely on forbidden-policy rows — so an attempted
/// distribution is not a delivered one, and only the delivered one is trained on.
///
/// Ordering the queue by deficit narrowed the gap and could not close it, because reordering can
/// only redistribute the scenarios that already exist. Closing it needs replacement generation:
/// when a bucket is short, build more scenarios OF THAT BUCKET and keep going.
///
/// Two rules the quota does not break:
///
///   * No row is ever discarded for belonging to a full bucket. Over-representation is corrected
///     by growing the under-represented buckets, so the corpus may finish larger than its row
///     target. That is the correct outcome, not an overrun.
///
///   * The gate is never weakened to help a bucket fill. unrequested-question rejects what it
///     rejects; the answer is more forbidden-policy scenarios, not a lower bar for them.
///
/// HARD ROWS ARE OUTSIDE THE QUOTA. Deliberately difficult forbidden-question compositions belong
/// in the hard/evaluation split, and counting them toward the main corpus mix would overweight
/// forbidden-policy rows in exactly the place that must stay production-shaped.
/// </summary>
public sealed class AcceptanceQuota(
    QuestionPolicyMix? questionMix = null, double noMustShare = 0.174)
{
    /// <summary>
    /// Declared rounding tolerance, in percentage points, for every bucket.
    ///
    /// 1.5 points is roughly 22 rows in a 1,500-row corpus. Tighter than the 8.7-point miss the
    /// last run delivered by an order of magnitude, and loose enough that the run terminates:
    /// each accepted row moves every share at once, so an arbitrarily tight band can be stepped
    /// over rather than landed on.
    /// </summary>
    public const double TolerancePoints = 1.5;

    private readonly QuestionPolicyMix _mix = questionMix ?? QuestionPolicyMix.FrozenRun1;
    private readonly Dictionary<string, int> _policy =
        new(StringComparer.OrdinalIgnoreCase) { ["none"] = 0, ["must_ask"] = 0, ["may_ask"] = 0 };

    private int _noMust;
    private int _total;

    /// <summary>Accepted rows counted toward the main corpus. Hard-split rows are excluded.</summary>
    public int Total => _total;

    /// <summary>Accepted rows routed to the hard/evaluation split, reported apart.</summary>
    public int HardAccepted { get; private set; }

    public void Record(ScenarioTruth scenario)
    {
        if (IsHard(scenario))
        {
            HardAccepted++;
            return;
        }

        _total++;
        var key = Normalise(scenario.Question.Policy);
        _policy[key] = _policy.GetValueOrDefault(key) + 1;
        if (!scenario.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress))
            _noMust++;
    }

    private static bool IsHard(ScenarioTruth scenario)
        => FamilySplitter.Assign(scenario.ScenarioFamilyId, scenario.HardCase) == "hard";

    public int AcceptedIn(string policy) => _policy.GetValueOrDefault(Normalise(policy));

    public double TargetShare(string policy) => Normalise(policy) switch
    {
        "must_ask" => _mix.AskRequired,
        "may_ask" => _mix.MayAsk,
        _ => _mix.Forbidden,
    };

    /// <summary>Every bucket the corpus is measured against: three policies, plus the no-must stratum.</summary>
    public IReadOnlyList<QuotaBucket> Buckets =>
    [
        new("question:none", _mix.Forbidden, _policy["none"], _total),
        new("question:must_ask", _mix.AskRequired, _policy["must_ask"], _total),
        new("question:may_ask", _mix.MayAsk, _policy["may_ask"], _total),
        new("stratum:no-must", noMustShare, _noMust, _total),
    ];

    /// <summary>
    /// Every bucket inside tolerance. This, together with the row target, is what "done" means —
    /// a run that has 1,500 rows in the wrong proportions has not finished.
    /// </summary>
    public bool Satisfied(double tolerancePoints = TolerancePoints)
        => _total > 0 && Buckets.All(b => Math.Abs(b.GapPoints) <= tolerancePoints);

    /// <summary>The buckets still short, worst first. What replacement generation should build.</summary>
    public IReadOnlyList<QuotaBucket> Deficient(double tolerancePoints = TolerancePoints)
        => Buckets.Where(b => b.GapPoints > tolerancePoints)
            .OrderByDescending(b => b.GapPoints)
            .ToList();

    /// <summary>
    /// How far below its target share a policy sits, as a fraction of the corpus. Used to order
    /// the existing queue; replacement generation handles what ordering cannot reach.
    /// </summary>
    public double Deficit(string policy)
    {
        var actual = _total == 0 ? 0d : AcceptedIn(policy) / (double)_total;
        return TargetShare(policy) - actual;
    }

    /// <summary>Work queue, most under-represented policy first; ties keep their original order.</summary>
    public IReadOnlyList<T> Prioritise<T>(IReadOnlyList<T> pending, Func<T, ScenarioTruth> scenarioOf)
        => pending
            .Select((item, index) => (item, index))
            .OrderByDescending(x => Deficit(scenarioOf(x.item).Question.Policy))
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    /// <summary>Does this scenario feed a bucket that is still short?</summary>
    public bool Feeds(QuotaBucket bucket, ScenarioTruth scenario)
    {
        if (IsHard(scenario))
            return false;
        return bucket.Name switch
        {
            "stratum:no-must" =>
                !scenario.ApprovedFacts.Any(f => f.Policy == FactPolicy.MustExpress),
            _ => bucket.Name == "question:" + Normalise(scenario.Question.Policy),
        };
    }

    private static string Normalise(string policy)
        => policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase) ? "must_ask"
            : policy.Equals("may_ask", StringComparison.OrdinalIgnoreCase) ? "may_ask"
            : "none";
}

/// <summary>
/// How many targets one scenario is worth.
///
/// Two variants of every scenario produced 684 duplicate rejections in the first pilot — 26.5% of
/// all rejection — because a second target only helps where the plan leaves room to say the same
/// thing differently. A terse plan with nothing required has one natural rendering ("It's all
/// set."), and asking for a second reliably produces the first one again.
///
/// So variants are spent where wording genuinely varies: something to say, and room to say it in.
/// Everything else gets one target, and the budget goes to more unique scenarios instead.
/// </summary>
public static class VariantPolicy
{
    public static int For(ScenarioTruth scenario, int maxVariants)
    {
        if (maxVariants <= 1)
            return Math.Max(1, maxVariants);

        // Terse plans converge on one phrasing however many times they are asked.
        if (scenario.Register.Verbosity.Equals("terse", StringComparison.OrdinalIgnoreCase))
            return 1;

        // Something to render: a required fact, or a scene the writer composes.
        var required = scenario.ApprovedFacts.Count(f => f.Policy == FactPolicy.MustExpress);
        var hasSubstance = required > 0 || scenario.Frame is not null
                           || scenario.Superseded.Count > 0;
        if (!hasSubstance)
            return 1;

        // Two facts, a frame, or an expansive register leave real room between renderings.
        var roomy = required > 1 || scenario.Frame is not null
                    || scenario.Register.Verbosity.Equals("expansive", StringComparison.OrdinalIgnoreCase);
        return roomy ? maxVariants : Math.Min(2, maxVariants);
    }
}
