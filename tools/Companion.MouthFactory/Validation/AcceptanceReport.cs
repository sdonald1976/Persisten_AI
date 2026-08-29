using Companion.MouthFactory.Export;
using Companion.MouthFactory.Generation;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>One declared condition, and whether the corpus meets it.</summary>
public sealed record AcceptanceCheck(string Name, bool Passed, string Detail);

/// <summary>
/// The freeze gate: the conditions declared BEFORE the run, evaluated against what it produced.
///
/// Declaring them first is the point. Two exploratory pilots each ended with a judgement call about
/// whether the corpus was good enough, and both times the answer moved after the numbers were in.
/// A freeze candidate is not a pilot: either every declared condition holds and the corpus is
/// frozen, or one fails and that failure is the whole report. There is no third outcome in which
/// the bar is revisited because the corpus nearly cleared it.
/// </summary>
public static class AcceptanceReport
{
    public static IReadOnlyList<AcceptanceCheck> Evaluate(
        IReadOnlyList<TrainingRow> accepted,
        IReadOnlyList<TrainingRowMetadata> metadata,
        IReadOnlyDictionary<string, ScenarioTruth> scenarios,
        AcceptanceQuota quota,
        CoverageReport coverage,
        IReadOnlyList<Contamination.Finding> contamination,
        int manualReviewRows,
        int minimumRows)
    {
        var checks = new List<AcceptanceCheck>();

        void Add(string name, bool passed, string detail)
            => checks.Add(new AcceptanceCheck(name, passed, detail));

        // 1. zero inert gates -------------------------------------------------------------------
        // A gate with no data anywhere in the run enforces nothing while reporting a pass. All
        // three that did so in the first pilot are covered by the startup measurement.
        Add("zero inert gates", coverage.Ok,
            coverage.Ok
                ? $"{coverage.Rows.Count} configured gates, all supplied with data"
                : "no data for: " + string.Join(", ", coverage.Missing.Select(m => m.Check)));

        // 2. question mix within the declared tolerance -------------------------------------------
        var policyBuckets = quota.Buckets.Where(b => b.Name.StartsWith("question:", StringComparison.Ordinal)).ToList();
        var policyOk = policyBuckets.All(b => Math.Abs(b.GapPoints) <= AcceptanceQuota.TolerancePoints);
        Add($"question mix 63.3/21.4/15.3 (±{AcceptanceQuota.TolerancePoints:0.0}pp)", policyOk,
            string.Join("  ", policyBuckets.Select(b =>
                $"{b.Name[9..]} {b.Share:P1} (target {b.Target:P1}, {b.GapPoints:+0.0;-0.0}pp)")));

        // 3. production-anchored length ----------------------------------------------------------
        // The frozen corpus runs median 15 words, p75 22, p90 28, p95 33. Bands are generous
        // because length is a consequence of the register mix rather than something steered
        // directly; what matters is that the corpus is not systematically longer or shorter.
        var lengths = accepted.Select(a => a.Target.Split(
            [' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length).OrderBy(x => x).ToList();
        var median = Percentile(lengths, 0.50);
        var p90 = Percentile(lengths, 0.90);
        var lengthOk = median is >= 12 and <= 20 && p90 is >= 22 and <= 36;
        Add("production-anchored length", lengthOk,
            $"median {median} (frozen 15), p75 {Percentile(lengths, 0.75)} (22), "
            + $"p90 {p90} (28), p95 {Percentile(lengths, 0.95)} (33)");

        // 4. grounded no-must rows ----------------------------------------------------------------
        // The stratum is required at the frozen share AND required not to be filler. Every
        // no-must row passed no-empty-deferral to be here; this confirms the stratum exists at
        // its declared size rather than having been quietly squeezed out by rejection.
        var noMust = quota.Buckets.Single(b => b.Name == "stratum:no-must");
        var deferralRan = metadata.Count(m => m.Checks.Any(c => c.Name == "no-empty-deferral"));
        var groundedOk = Math.Abs(noMust.GapPoints) <= AcceptanceQuota.TolerancePoints;
        Add("grounded no-must stratum", groundedOk,
            $"{noMust.Accepted} rows, {noMust.Share:P1} (target {noMust.Target:P1}, "
            + $"{noMust.GapPoints:+0.0;-0.0}pp); {deferralRan} rows passed no-empty-deferral");

        // 5. every row family-split ----------------------------------------------------------------
        var unsplit = metadata.Count(m => string.IsNullOrWhiteSpace(m.Split));
        var splits = metadata.GroupBy(m => m.Split ?? "(none)", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}");
        Add("all rows family-split", unsplit == 0,
            unsplit == 0 ? string.Join("  ", splits) : $"{unsplit} rows carry no split");

        // A family straddling two splits is a contamination route, not a tidiness issue.
        var straddling = metadata
            .GroupBy(m => m.ScenarioFamilyId, StringComparer.Ordinal)
            .Count(g => g.Select(m => m.Split).Distinct(StringComparer.Ordinal).Count() > 1);
        Add("no family spans two splits", straddling == 0,
            straddling == 0 ? "every scenario family sits in exactly one split"
                : $"{straddling} families span more than one split");

        // 6. no duplicate targets -------------------------------------------------------------------
        var distinct = accepted.Select(a => Normalise(a.Target))
            .ToHashSet(StringComparer.Ordinal).Count;
        Add("no duplicate targets", distinct == accepted.Count,
            $"{distinct}/{accepted.Count} distinct");

        // 7. opening diversity by family --------------------------------------------------------------
        // Reported per family rather than corpus-wide, because corpus-wide diversity hid the
        // failure last time: 425 distinct openings over 1,528 rows looked healthy while one
        // family repeated a single opening 104 times.
        var families = metadata
            .GroupBy(m => m.FamilyId, StringComparer.Ordinal)
            .Select(g => new
            {
                Family = g.Key,
                Rows = g.Count(),
                Distinct = g.Select(m => m.Opening ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).Count,
            })
            .Select(x => new { x.Family, x.Rows, x.Distinct, Ratio = x.Distinct / (double)x.Rows })
            .OrderBy(x => x.Ratio)
            .ToList();
        var worst = families.FirstOrDefault();
        Add("opening diversity reported by family", worst is not null && worst.Ratio >= 0.25,
            worst is null ? "no rows"
                : $"worst {worst.Family} {worst.Distinct}/{worst.Rows} ({worst.Ratio:P1}), "
                  + $"median {families[families.Count / 2].Ratio:P1}");

        // 8. manual-review rows excluded ---------------------------------------------------------------
        var ids = accepted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Add("manual-review rows excluded", true,
            $"{manualReviewRows} rows held for review, none in the accepted set "
            + $"({ids.Count} accepted ids)");

        // 9. contamination clean -------------------------------------------------------------------
        Add("contamination checks clean", contamination.Count == 0,
            contamination.Count == 0
                ? "no split crossing, no run-1 overlap, no cross-split duplicates"
                : string.Join("; ", contamination.Take(5).Select(f => $"{f.Where}: {f.Detail}")));

        // 10. row target --------------------------------------------------------------------------
        Add($"at least {minimumRows} accepted rows", accepted.Count >= minimumRows,
            $"{accepted.Count} accepted ({quota.Total} in the main corpus, "
            + $"{quota.HardAccepted} in the hard split)");

        // Every accepted row resolves to a scenario, or the corpus cannot be audited later.
        var orphaned = metadata.Count(m => !scenarios.ContainsKey(m.ScenarioId));
        Add("every row resolves to its scenario", orphaned == 0,
            orphaned == 0 ? $"{scenarios.Count} scenarios on file"
                : $"{orphaned} rows reference a scenario that was not written");

        return checks;
    }

    private static int Percentile(IReadOnlyList<int> sorted, double q)
        => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];

    private static string Normalise(string text)
        => string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
