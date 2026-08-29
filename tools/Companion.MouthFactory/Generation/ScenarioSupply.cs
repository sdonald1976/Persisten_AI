using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// More scenarios of a particular kind, on demand.
///
/// The run needs this because a quota expressed in ACCEPTED rows cannot be met from a fixed work
/// list. Reordering the queue by deficit — which is what the last run did — redistributes the
/// scenarios that happen to exist; it cannot conjure a forbidden-policy scenario once they have
/// all been attempted and two thirds of them rejected. When a bucket is short and the queue is
/// dry, the only honest options are to build more of that bucket or to stop and say the quota was
/// not met. This is the first one.
/// </summary>
public interface IScenarioSupply
{
    /// <summary>
    /// Up to <paramref name="count"/> further scenarios for which <paramref name="wanted"/> holds,
    /// continuing the deterministic index sequence. Fewer than asked for means the supply is
    /// exhausted within its search budget, which is a reported outcome rather than an error.
    /// </summary>
    IReadOnlyList<ScenarioTruth> More(Func<ScenarioTruth, bool> wanted, int count);
}

/// <summary>
/// Draws further scenarios from the same generator, families and seed as the initial build.
///
/// Determinism is preserved exactly the way it always was: a scenario is a pure function of
/// (family, index, seed), and this simply continues past the index the initial build stopped at.
/// Nothing is re-rolled, so a resumed run produces the same replacement scenarios in the same
/// order, and two runs of the same command produce identical corpora.
///
/// The generator has no way to build "a forbidden-policy scenario" to order — the policy is drawn
/// inside the build from the frozen mix, and forcing it would replace an empirical distribution
/// with a demand. So this generates and filters. That costs nothing: scenario construction is
/// arithmetic, and only the ones that pass the filter ever reach a model.
/// </summary>
public sealed class GeneratorScenarioSupply(
    ScenarioGenerator generator,
    IReadOnlyList<FamilySpec> families,
    IReadOnlyDictionary<string, int> startIndex,
    int searchBudgetPerCall = 20000) : IScenarioSupply
{
    private readonly Dictionary<string, int> _next =
        families.ToDictionary(f => f.Id, f => startIndex.GetValueOrDefault(f.Id), StringComparer.Ordinal);

    /// <summary>Total scenarios built while searching, including the ones filtered away.</summary>
    public int Examined { get; private set; }

    /// <summary>
    /// Every scenario this supply handed back. The run writes these alongside the initial build,
    /// because a scenario that produced a row and is not in scenarios.jsonl is a row the export
    /// cannot resolve a family or a split for.
    /// </summary>
    public IReadOnlyList<ScenarioTruth> Built => _built;

    private readonly List<ScenarioTruth> _built = [];

    public IReadOnlyList<ScenarioTruth> More(Func<ScenarioTruth, bool> wanted, int count)
    {
        var found = new List<ScenarioTruth>();
        var examined = 0;

        // Round-robin across families so a top-up does not quietly turn into one family's corpus.
        // The curriculum's strata are a design decision; a quota is not a licence to abandon them.
        while (found.Count < count && examined < searchBudgetPerCall)
        {
            var progressed = false;
            foreach (var family in families)
            {
                if (found.Count >= count || examined >= searchBudgetPerCall)
                    break;

                var index = _next[family.Id]++;
                var scenario = generator.Build(family, index);
                examined++;
                progressed = true;
                if (wanted(scenario))
                {
                    found.Add(scenario);
                    _built.Add(scenario);
                }
            }

            if (!progressed)
                break;
        }

        Examined += examined;
        return found;
    }
}
