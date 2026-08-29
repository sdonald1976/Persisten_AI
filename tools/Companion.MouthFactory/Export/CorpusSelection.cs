using System.Security.Cryptography;
using System.Text;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Export;

/// <summary>What the frozen corpus is required to contain.</summary>
public sealed record SelectionRequest
{
    public required int TotalRows { get; init; }

    /// <summary>Rows per question policy. Must sum to <see cref="TotalRows"/>.</summary>
    public required IReadOnlyDictionary<string, int> PolicyTargets { get; init; }

    /// <summary>Rows whose plan requires nothing, spread across policies as the pool has them.</summary>
    public required int NoMustRows { get; init; }

    /// <summary>Floor on distinct openings within any family's selected rows.</summary>
    public double MinOpeningRatio { get; init; } = 0.25;

    /// <summary>Smallest corpus worth freezing if the full request cannot be met.</summary>
    public int MinimumRows { get; init; } = 1500;
}

public sealed record FamilyAllocation(string Family, int Pool, int DistinctOpenings, int Cap, int Selected);

public sealed record SelectionResult
{
    public required bool Feasible { get; init; }
    public required IReadOnlyList<string> SelectedIds { get; init; }
    public required string PoolHash { get; init; }
    public required string SelectionHash { get; init; }
    public required string Algorithm { get; init; }
    public required long Seed { get; init; }
    public required IReadOnlyList<FamilyAllocation> Families { get; init; }
    public required IReadOnlyDictionary<string, int> PolicyCounts { get; init; }
    public required int NoMustSelected { get; init; }
    public required IReadOnlyList<string> Conflicts { get; init; }
}

/// <summary>
/// Chooses which accepted rows become the frozen corpus.
///
/// WHY SELECTION RATHER THAN REGENERATION. A generation run cannot be steered precisely to a
/// distribution: the gates reject unevenly, so the pool it produces is whatever survived. Two runs
/// tried to hit 63.3/21.4/15.3 by generating and both missed, the second still converging when its
/// unit budget ran out. But the pool it left contains 2,128 forbidden-policy rows where 1,266 are
/// wanted — the corpus was there, mixed in with the surplus. Choosing a subset is exact where
/// generating an exact one is not.
///
/// Every accepted row stays in the candidate store. Selection decides what is exported, never what
/// is kept, so nothing measured here is destroyed and a later freeze can select differently from
/// the same pool.
///
/// DETERMINISM is a property of the ordering, not of a seeded shuffle. Every comparison below is
/// ordinal on stable strings — family id, opening text, row id — so the same pool yields the same
/// corpus on any machine, in any run, forever. The seed is recorded because the pool it produced
/// depends on it, not because anything here consumes randomness.
/// </summary>
public static class CorpusSelection
{
    public const string Algorithm = "balanced-opening-roundrobin/1.0";

    /// <summary>One row as the selector sees it.</summary>
    public sealed record Candidate(
        string Id, string Family, string Policy, bool NoMust, string Opening, string Split);

    public static SelectionResult Select(
        IReadOnlyList<Candidate> pool, SelectionRequest request, long seed)
    {
        var poolHash = HashPool(pool);
        var conflicts = new List<string>();

        // ---- family caps --------------------------------------------------------------------
        // Selecting n rows from a family that has d distinct openings among them gives a ratio of
        // min(n, d)/n, so the 25% floor is exactly n <= 4d. b9 has 25 distinct openings across 150
        // accepted rows: at most 100 of them can be exported without the family falling below the
        // floor, and exporting all 150 is what failed the last freeze.
        var byFamily = pool
            .GroupBy(c => c.Family, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var caps = byFamily.ToDictionary(
            kv => kv.Key,
            kv => Math.Min(
                kv.Value.Count,
                (int)Math.Floor(DistinctOpenings(kv.Value) / request.MinOpeningRatio)),
            StringComparer.Ordinal);

        // ---- feasibility, before selecting anything -------------------------------------------
        var capacity = caps.Values.Sum();
        if (capacity < request.TotalRows)
            conflicts.Add(
                $"opening-diversity caps allow at most {capacity} rows, {request.TotalRows} requested");

        foreach (var (policy, target) in request.PolicyTargets)
        {
            var available = pool.Count(c => Same(c.Policy, policy));
            if (available < target)
                conflicts.Add($"policy '{policy}': {available} accepted, {target} requested");
        }

        var noMustAvailable = pool.Count(c => c.NoMust);
        if (noMustAvailable < request.NoMustRows)
            conflicts.Add(
                $"no-must stratum: {noMustAvailable} accepted, {request.NoMustRows} requested");

        if (request.PolicyTargets.Values.Sum() != request.TotalRows)
            conflicts.Add(
                $"policy targets sum to {request.PolicyTargets.Values.Sum()}, "
                + $"total is {request.TotalRows}");

        if (conflicts.Count > 0)
            return Infeasible(poolHash, seed, caps, byFamily, conflicts);

        // ---- ordering -------------------------------------------------------------------------
        // Rows are laid out in WAVES: wave 0 takes one row from every distinct opening, wave 1 the
        // second row of each, and so on. Walking waves in order means the corpus is built from the
        // most varied rows first and repeats only when it must, which is what keeps a family's
        // distinct-opening ratio as high as its pool allows rather than merely above the floor.
        var ordered = pool
            .GroupBy(c => (c.Family, Bucket: c.Opening.ToLowerInvariant()))
            .SelectMany(g => g
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select((c, wave) => (Row: c, Wave: wave, Bucket: g.Key.Bucket)))
            .OrderBy(x => x.Wave)
            .ThenBy(x => x.Row.Family, StringComparer.Ordinal)
            .ThenBy(x => x.Bucket, StringComparer.Ordinal)
            .ThenBy(x => x.Row.Id, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();

        var selected = new List<Candidate>();
        var takenPerFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        var takenPerPolicy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var chosen = new HashSet<string>(StringComparer.Ordinal);

        bool Room(Candidate c, IReadOnlyDictionary<string, int> policyBudget)
            => takenPerFamily.GetValueOrDefault(c.Family) < caps[c.Family]
               && takenPerPolicy.GetValueOrDefault(Normalise(c.Policy))
                   < policyBudget.GetValueOrDefault(Normalise(c.Policy));

        void Take(Candidate c)
        {
            selected.Add(c);
            chosen.Add(c.Id);
            takenPerFamily[c.Family] = takenPerFamily.GetValueOrDefault(c.Family) + 1;
            takenPerPolicy[Normalise(c.Policy)] =
                takenPerPolicy.GetValueOrDefault(Normalise(c.Policy)) + 1;
        }

        // ---- phase 1: the no-must stratum -----------------------------------------------------
        // Spread across policies as the POOL has them rather than by an invented split. A no-must
        // turn is an acknowledgement, and whether one also carries a licensed question is a fact
        // about the scenario, not a knob: forcing a ratio here would manufacture combinations the
        // curriculum never produced.
        var noMustByPolicy = request.PolicyTargets.Keys.ToDictionary(
            p => p,
            p => pool.Count(c => c.NoMust && Same(c.Policy, p)),
            StringComparer.OrdinalIgnoreCase);
        var noMustBudget = Apportion(noMustByPolicy, request.NoMustRows);
        foreach (var policy in noMustBudget.Keys.ToList())
            noMustBudget[policy] = Math.Min(
                noMustBudget[policy], request.PolicyTargets.GetValueOrDefault(policy));

        foreach (var candidate in ordered.Where(c => c.NoMust))
            if (Room(candidate, noMustBudget))
                Take(candidate);

        // ---- phase 2: fill the policy quotas from must-bearing rows ---------------------------
        // The no-must stratum is a quota of exactly N, not a floor. Letting phase 2 keep taking
        // no-must rows once phase 1 has filled it overshoots the stratum and starves the
        // must-bearing majority: the first run of this selector delivered 446 where 348 were
        // asked for, and failed its own declared condition by 4.9 points.
        var noMustTaken = selected.Count;
        foreach (var candidate in ordered)
        {
            if (chosen.Contains(candidate.Id))
                continue;
            if (candidate.NoMust && noMustTaken >= request.NoMustRows)
                continue;
            if (!Room(candidate, request.PolicyTargets))
                continue;
            Take(candidate);
            if (candidate.NoMust)
                noMustTaken++;
        }

        // ---- did every quota fill? -------------------------------------------------------------
        foreach (var (policy, target) in request.PolicyTargets)
        {
            var got = takenPerPolicy.GetValueOrDefault(Normalise(policy));
            if (got < target)
                conflicts.Add(
                    $"policy '{policy}': filled {got} of {target} before family opening-diversity "
                    + "caps were exhausted");
        }

        var noMustSelected = selected.Count(c => c.NoMust);
        if (noMustSelected < request.NoMustRows)
            conflicts.Add(
                $"no-must stratum: filled {noMustSelected} of {request.NoMustRows} within the "
                + "policy and family caps");

        var ids = selected.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new SelectionResult
        {
            Feasible = conflicts.Count == 0,
            SelectedIds = ids,
            PoolHash = poolHash,
            SelectionHash = HashIds(ids),
            Algorithm = Algorithm,
            Seed = seed,
            Families = Allocations(caps, byFamily, takenPerFamily),
            PolicyCounts = takenPerPolicy,
            NoMustSelected = noMustSelected,
            Conflicts = conflicts,
        };
    }

    /// <summary>
    /// The largest corpus that can ACTUALLY be selected in the requested proportions, at or above
    /// the request's floor.
    ///
    /// Found by trying, not by arithmetic. The three obvious bounds — family caps, per-policy
    /// availability, no-must availability — are each necessary and none is sufficient, because
    /// they interact: a policy can have rows to spare that sit in families whose opening-diversity
    /// caps are already full. An upper bound that cannot be selected is worse than no answer,
    /// since it would be reported as achievable.
    ///
    /// Returns 0 when nothing at or above the floor works.
    /// </summary>
    public static int LargestFeasible(
        IReadOnlyList<Candidate> pool, SelectionRequest request, long seed = 0)
    {
        // Start from the cheapest upper bound and walk down in whole percent of the request, so
        // the proportions stay exactly what was asked for at every size tried.
        var ceiling = Math.Min(
            request.TotalRows,
            Math.Min(
                pool.GroupBy(c => c.Family, StringComparer.Ordinal)
                    .Sum(g => Math.Min(g.Count(),
                        (int)Math.Floor(DistinctOpenings(g.ToList()) / request.MinOpeningRatio))),
                pool.Count));

        // Binary search. Feasibility is monotone in size for a fixed mixture - every constraint
        // is an upper bound that only loosens as the request shrinks - so the largest workable
        // size is the boundary between the two halves.
        if (ceiling < request.MinimumRows)
            return 0;

        var low = request.MinimumRows;
        var high = ceiling;
        var best = 0;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (Select(pool, Scale(request, mid), seed).Feasible)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return best;
    }

    /// <summary>The same mixture at a different size. Largest-remainder, so the parts still sum.</summary>
    private static SelectionRequest Scale(SelectionRequest request, int size)
    {
        var policy = Apportion(
            request.PolicyTargets.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            size);
        return request with
        {
            TotalRows = size,
            PolicyTargets = policy,
            NoMustRows = (int)Math.Round(
                request.NoMustRows / (double)request.TotalRows * size, MidpointRounding.ToZero),
        };
    }

    /// <summary>
    /// Whole numbers summing exactly to <paramref name="total"/>, in proportion to the weights.
    /// Largest-remainder, with ordinal tie-breaking so the result never depends on dictionary order.
    /// </summary>
    private static Dictionary<string, int> Apportion(
        IReadOnlyDictionary<string, int> weights, int total)
    {
        var sum = weights.Values.Sum();
        if (sum == 0)
            return weights.Keys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);

        var exact = weights.ToDictionary(kv => kv.Key, kv => kv.Value / (double)sum * total,
            StringComparer.OrdinalIgnoreCase);
        var result = exact.ToDictionary(kv => kv.Key, kv => (int)Math.Floor(kv.Value),
            StringComparer.OrdinalIgnoreCase);

        var remaining = total - result.Values.Sum();
        foreach (var key in exact
                     .OrderByDescending(kv => kv.Value - Math.Floor(kv.Value))
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Select(kv => kv.Key))
        {
            if (remaining-- <= 0)
                break;
            result[key]++;
        }
        return result;
    }

    private static int DistinctOpenings(IReadOnlyList<Candidate> rows)
        => rows.Select(c => c.Opening.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal).Count;

    private static IReadOnlyList<FamilyAllocation> Allocations(
        IReadOnlyDictionary<string, int> caps,
        IReadOnlyDictionary<string, List<Candidate>> byFamily,
        IReadOnlyDictionary<string, int> taken)
        => byFamily
            .Select(kv => new FamilyAllocation(
                kv.Key, kv.Value.Count, DistinctOpenings(kv.Value),
                caps[kv.Key], taken.GetValueOrDefault(kv.Key)))
            .OrderBy(a => a.Family, StringComparer.Ordinal)
            .ToList();

    private static SelectionResult Infeasible(
        string poolHash, long seed,
        IReadOnlyDictionary<string, int> caps,
        IReadOnlyDictionary<string, List<Candidate>> byFamily,
        IReadOnlyList<string> conflicts)
        => new()
        {
            Feasible = false, SelectedIds = [], PoolHash = poolHash, SelectionHash = HashIds([]),
            Algorithm = Algorithm, Seed = seed,
            Families = Allocations(caps, byFamily, new Dictionary<string, int>(StringComparer.Ordinal)),
            PolicyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            NoMustSelected = 0, Conflicts = conflicts,
        };

    /// <summary>
    /// The whole candidate pool, as one hash. Recorded in the manifest so a later run can prove it
    /// selected from the same rows: a corpus is only reproducible if the set it was drawn from is
    /// identified, not just the algorithm that drew it.
    /// </summary>
    public static string HashPool(IReadOnlyList<Candidate> pool)
        => HashIds(pool
            .Select(c => string.Join('', c.Id, c.Family, c.Policy, c.NoMust, c.Split))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList());

    private static string HashIds(IReadOnlyList<string> lines)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    private static bool Same(string a, string b)
        => Normalise(a).Equals(Normalise(b), StringComparison.Ordinal);

    private static string Normalise(string policy)
        => policy.Equals("must_ask", StringComparison.OrdinalIgnoreCase) ? "must_ask"
            : policy.Equals("may_ask", StringComparison.OrdinalIgnoreCase) ? "may_ask"
            : "none";
}
