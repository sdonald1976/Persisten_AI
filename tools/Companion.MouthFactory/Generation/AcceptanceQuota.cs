using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// Steers the ACCEPTED question-policy mix toward the frozen anchor, by choosing what to attempt
/// next rather than by discarding rows.
///
/// The pilot assigned policies correctly and still shipped the wrong corpus. Scenarios were drawn
/// at 60.6% forbidden / 23.7% must_ask / 15.6% may_ask, close to the 63.3/21.4/15.3 anchor — but
/// unrequested-question rejected 1,382 rows, almost all of them forbidden-policy, and what
/// survived was 47.6% forbidden. Rejection is not uniform across policies, so an attempted
/// distribution is not a delivered one, and only the delivered one is trained on.
///
/// The correction is to attempt more of whatever is falling behind. A row is never rejected for
/// belonging to an over-represented bucket: that would throw away good work and bias the corpus
/// toward whatever the writer happens to find easy. Ordering the queue costs nothing and converges
/// on the target as long as scenarios of the deficient policy remain.
/// </summary>
public sealed class AcceptanceQuota(QuestionPolicyMix target)
{
    private readonly Dictionary<string, int> _accepted =
        new(StringComparer.OrdinalIgnoreCase) { ["none"] = 0, ["must_ask"] = 0, ["may_ask"] = 0 };

    public int Total => _accepted.Values.Sum();

    public void Record(string policy)
    {
        var key = Normalise(policy);
        _accepted[key] = _accepted.GetValueOrDefault(key) + 1;
    }

    public int AcceptedIn(string policy) => _accepted.GetValueOrDefault(Normalise(policy));

    public double TargetShare(string policy) => Normalise(policy) switch
    {
        "must_ask" => target.AskRequired,
        "may_ask" => target.MayAsk,
        _ => target.Forbidden,
    };

    /// <summary>
    /// How far below its target share a policy currently sits, as a fraction of the corpus.
    /// Positive means under-represented and worth attempting next; negative means ahead.
    ///
    /// With nothing accepted yet every deficit equals the target share, so the first batches are
    /// drawn in target proportions rather than in queue order.
    /// </summary>
    public double Deficit(string policy)
    {
        var total = Total;
        var actual = total == 0 ? 0d : AcceptedIn(policy) / (double)total;
        return TargetShare(policy) - actual;
    }

    /// <summary>
    /// The work queue, most under-represented policy first. Ties keep their original order, so a
    /// run remains deterministic and a resumed run continues in the same sequence.
    /// </summary>
    public IReadOnlyList<T> Prioritise<T>(IReadOnlyList<T> pending, Func<T, ScenarioTruth> scenarioOf)
        => pending
            .Select((item, index) => (item, index))
            .OrderByDescending(x => Deficit(scenarioOf(x.item).Question.Policy))
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();

    private static string Normalise(string policy)
        => policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase) ? "must_ask"
            : policy.Equals("may_ask", StringComparison.OrdinalIgnoreCase) ? "may_ask"
            : "none";
}

/// <summary>
/// How many targets one scenario is worth.
///
/// Two variants of every scenario produced 684 duplicate rejections in the pilot — 26.5% of all
/// rejection — because a second target only helps where the plan leaves room to say the same thing
/// differently. A terse plan with nothing required has one natural rendering ("It's all set."), and
/// asking for a second reliably produces the first one again.
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
