using Companion.MouthFactory.Export;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Selection: choosing which accepted rows become the frozen corpus.
///
/// Two generation runs tried to hit 63.3/21.4/15.3 by generating and both missed, the second still
/// converging when its unit budget ran out. The pool it left held 2,128 forbidden-policy rows where
/// 1,266 were wanted — the corpus was there, mixed in with the surplus. Choosing a subset is exact
/// where generating an exact one is not, and these tests pin that exactness.
/// </summary>
public class CorpusSelectionTests
{
    private static SelectionRequest Request(int total = 200) => new()
    {
        TotalRows = total,
        PolicyTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = total * 633 / 1000,
            ["must_ask"] = total * 214 / 1000,
            ["may_ask"] = total - total * 633 / 1000 - total * 214 / 1000,
        },
        NoMustRows = total * 174 / 1000,
        MinimumRows = 100,
    };

    // ---- the quotas are exact -------------------------------------------------------------------

    [Fact]
    public void EveryPolicyQuotaIsFilledExactly()
    {
        var result = CorpusSelection.Select(Pool(), Request(), seed: 1);

        Assert.True(result.Feasible, string.Join("; ", result.Conflicts));
        Assert.Equal(126, result.PolicyCounts["none"]);
        Assert.Equal(42, result.PolicyCounts["must_ask"]);
        Assert.Equal(32, result.PolicyCounts["may_ask"]);
        Assert.Equal(200, result.SelectedIds.Count);
    }

    [Fact]
    public void TheNoMustStratumIsAQuotaNotAFloor()
    {
        // The first version of this selector let the second phase keep taking no-must rows after
        // the stratum was full: it delivered 446 where 348 were asked for and failed its own
        // declared condition by 4.9 points.
        var result = CorpusSelection.Select(Pool(), Request(), seed: 1);

        Assert.Equal(34, result.NoMustSelected);
    }

    [Fact]
    public void NoFamilyFallsBelowTheOpeningDiversityFloor()
    {
        var result = CorpusSelection.Select(Pool(), Request(), seed: 1);

        foreach (var family in result.Families.Where(f => f.Selected > 0))
        {
            var distinct = Math.Min(family.Selected, family.DistinctOpenings);
            Assert.True(distinct / (double)family.Selected >= 0.25,
                $"{family.Family}: {distinct}/{family.Selected}");
        }
    }

    [Fact]
    public void AFamilyIsCappedAtFourTimesItsDistinctOpenings()
    {
        // b9 had 25 distinct openings across 150 accepted rows, so at most 100 can be exported
        // without the family dropping under the floor. Exporting all 150 failed the last freeze.
        var pool = Pool().Concat(Rows("b9", count: 150, distinctOpenings: 25, policy: "none")).ToList();
        var result = CorpusSelection.Select(pool, Request(400), seed: 1);

        var b9 = result.Families.Single(f => f.Family == "b9");
        Assert.Equal(100, b9.Cap);
        Assert.True(b9.Selected <= 100, $"selected {b9.Selected}");
    }

    // ---- determinism ------------------------------------------------------------------------------

    [Fact]
    public void TheSamePoolAlwaysProducesTheSameCorpus()
    {
        var first = CorpusSelection.Select(Pool(), Request(), seed: 1);
        var second = CorpusSelection.Select(Pool(), Request(), seed: 1);

        Assert.Equal(first.SelectedIds, second.SelectedIds);
        Assert.Equal(first.SelectionHash, second.SelectionHash);
        Assert.Equal(first.PoolHash, second.PoolHash);
    }

    [Fact]
    public void PoolOrderDoesNotChangeTheResult()
    {
        // Determinism is a property of the ordering, not of the order rows happen to arrive in.
        var shuffled = Pool().OrderByDescending(c => c.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(
            CorpusSelection.Select(Pool(), Request(), seed: 1).SelectionHash,
            CorpusSelection.Select(shuffled, Request(), seed: 1).SelectionHash);
    }

    [Fact]
    public void ADifferentPoolProducesADifferentPoolHash()
    {
        var bigger = Pool().Concat(Rows("zz", 10, 10, "none")).ToList();

        Assert.NotEqual(
            CorpusSelection.Select(Pool(), Request(), seed: 1).PoolHash,
            CorpusSelection.Select(bigger, Request(), seed: 1).PoolHash);
    }

    // ---- diversity is preferred, not merely permitted -----------------------------------------------

    [Fact]
    public void DistinctOpeningsAreTakenBeforeRepeats()
    {
        // Wave ordering: one row from every opening before a second from any of them. A selector
        // that merely respected the floor could take four copies of one opening and still pass.
        var pool = Rows("solo", count: 40, distinctOpenings: 20, policy: "none", noMust: false)
            .ToList();
        var request = new SelectionRequest
        {
            TotalRows = 20,
            PolicyTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["none"] = 20, ["must_ask"] = 0, ["may_ask"] = 0,
            },
            NoMustRows = 0,
            MinimumRows = 1,
        };

        var result = CorpusSelection.Select(pool, request, seed: 1);
        var openings = pool
            .Where(c => result.SelectedIds.Contains(c.Id))
            .Select(c => c.Opening)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(20, openings.Count);       // every one distinct
    }

    [Fact]
    public void EveryFamilyIsRepresented()
    {
        var result = CorpusSelection.Select(Pool(), Request(), seed: 1);

        Assert.All(result.Families, f => Assert.True(f.Selected > 0, f.Family));
    }

    [Fact]
    public void SelectionNeverMovesARowBetweenSplits()
    {
        var pool = Pool();
        var result = CorpusSelection.Select(pool, Request(), seed: 1);
        var chosen = result.SelectedIds.ToHashSet(StringComparer.Ordinal);

        // A family sits in exactly one split, and selecting a subset cannot change that.
        foreach (var family in pool.Where(c => chosen.Contains(c.Id)).GroupBy(c => c.Family))
            Assert.Single(family.Select(c => c.Split).Distinct(StringComparer.Ordinal));
    }

    // ---- infeasibility is reported, never silently narrowed --------------------------------------------

    [Fact]
    public void AnImpossibleRequestIsRefusedWithItsConflicts()
    {
        var result = CorpusSelection.Select(Pool(), Request(total: 100_000), seed: 1);

        Assert.False(result.Feasible);
        Assert.NotEmpty(result.Conflicts);
        Assert.Empty(result.SelectedIds);
    }

    [Fact]
    public void TheLargestFeasibleCorpusIsComputable()
    {
        var pool = Pool();
        var request = Request(total: 100_000) with { MinimumRows = 100 };
        var largest = CorpusSelection.LargestFeasible(pool, request, seed: 1);

        Assert.True(largest >= 100, $"largest {largest}");
        Assert.True(largest < 100_000);

        // The point of the search: the size it reports can actually be selected. An upper bound
        // that cannot be realised would be reported as achievable, which is worse than no answer.
        Assert.True(
            CorpusSelection.LargestFeasible(pool, request with { MinimumRows = largest }, seed: 1)
                == largest,
            $"reported {largest} as feasible but it is not selectable at that size");
    }

    [Fact]
    public void MismatchedTargetsAreCaughtRatherThanSilentlyRebalanced()
    {
        var request = new SelectionRequest
        {
            TotalRows = 100,
            PolicyTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["none"] = 50, ["must_ask"] = 20, ["may_ask"] = 20,
            },
            NoMustRows = 10,
        };

        var result = CorpusSelection.Select(Pool(), request, seed: 1);

        Assert.False(result.Feasible);
        Assert.Contains(result.Conflicts, c => c.Contains("sum to 90"));
    }

    // ---- fixtures ---------------------------------------------------------------------------------------

    /// <summary>
    /// A pool wide enough that the quotas are reachable and the caps are not all binding: nine
    /// families, mixed policies, a realistic share of no-must rows.
    /// </summary>
    private static List<CorpusSelection.Candidate> Pool()
    {
        var pool = new List<CorpusSelection.Candidate>();
        string[] families = ["a1", "a5", "a7b", "b1", "b3", "b4", "b5", "b8", "b11"];
        foreach (var family in families)
        {
            pool.AddRange(Rows(family, 60, 30, "none"));
            pool.AddRange(Rows(family, 24, 12, "must_ask"));
            pool.AddRange(Rows(family, 20, 10, "may_ask"));
        }
        return pool;
    }

    private static IEnumerable<CorpusSelection.Candidate> Rows(
        string family, int count, int distinctOpenings, string policy, bool? noMust = null)
    {
        // Splits are family-wide, exactly as FamilySplitter assigns them.
        var split = FamilySplitter.Assign(family + "-fam", hardCase: false);
        for (var i = 0; i < count; i++)
            yield return new CorpusSelection.Candidate(
                Id: $"{family}-{policy}-{i:D4}",
                Family: family,
                Policy: policy,
                // Roughly a fifth carry no required item, as the frozen corpus does.
                NoMust: noMust ?? i % 5 == 0,
                Opening: $"{family}-opening-{i % distinctOpenings:D3}",
                Split: split);
    }
}
