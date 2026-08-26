using System.Security.Cryptography;
using System.Text;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Export;

public sealed record SplitPlan
{
    public required IReadOnlyDictionary<string, string> FamilyToSplit { get; init; }
    public required IReadOnlyList<string> UnseenCompositions { get; init; }
}

/// <summary>
/// Family-aware splitting, decided BEFORE targets are generated.
///
/// Two rules, both structural:
///
///   * Every row of a scenario family goes to one split. Near-variants, paraphrases and the
///     several targets of one hidden state cannot be separated, because a validation set that
///     contains a reworded copy of a training row measures memorization and reports it as skill.
///
///   * The assignment is a hash of the family id, not a shuffle. The same family lands in the
///     same split on every machine and every rerun, so a corpus regenerated after a crash does
///     not silently reshuffle its own validation set.
/// </summary>
public static class FamilySplitter
{
    public static SplitPlan Plan(
        IReadOnlyList<ScenarioTruth> scenarios,
        double validationShare = 0.10,
        double testShare = 0.10,
        IReadOnlySet<string>? hardFamilies = null)
    {
        var families = scenarios
            .Select(s => s.ScenarioFamilyId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in families)
        {
            if (hardFamilies?.Contains(family) == true)
            {
                assignment[family] = "hard";
                continue;
            }

            // Deterministic per family: stable across runs, machines and orderings.
            var bucket = Bucket(family);
            assignment[family] = bucket < validationShare ? "validation"
                : bucket < validationShare + testShare ? "test"
                : "train";
        }

        return new SplitPlan
        {
            FamilyToSplit = assignment,
            UnseenCompositions = SelectUnseen(scenarios),
        };
    }

    /// <summary>
    /// The held-out compositions (R5 B10), selected MECHANICALLY from Plan/4 structure — never by
    /// substring similarity, and never by anyone's judgement about which look interesting.
    ///
    /// The structural signature of a scenario is its multiset of expression policies, its question
    /// policy, whether it carries a frame and which transition. A composition is held out when the
    /// hash of that signature falls in the reserved band, so "which combinations did the model
    /// never see" is a property of the structure rather than of the wording.
    /// </summary>
    public static IReadOnlyList<string> SelectUnseen(
        IReadOnlyList<ScenarioTruth> scenarios, double share = 0.05)
        => scenarios
            .Select(StructuralSignature)
            .Distinct(StringComparer.Ordinal)
            .Where(sig => Bucket(sig) < share)
            .OrderBy(sig => sig, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A scenario's structure, as a string. Deliberately contains no prose: two scenarios with
    /// completely different words and the same control structure have the same signature, which
    /// is exactly what "unseen composition" should mean for a protocol-following model.
    /// </summary>
    public static string StructuralSignature(ScenarioTruth s)
    {
        var policies = s.ApprovedFacts
            .GroupBy(f => f.Policy)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key.ToString().ToLowerInvariant()}x{g.Count()}");

        var parts = new List<string>(policies)
        {
            $"q={s.Question.Policy.ToLowerInvariant()}",
            $"sup={s.Superseded.Count}",
            $"unk={s.EpistemicUnknowns.Count}",
            $"amb={s.IntentionalAmbiguities.Count}",
            $"frame={(s.Frame is null ? "none" : s.Frame.Transition)}",
        };
        return string.Join('|', parts);
    }

    /// <summary>Stable [0,1) from a string. SHA-256 so it is identical on every platform.</summary>
    private static double Bucket(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToUInt32(hash, 0);
        return value / (double)uint.MaxValue;
    }
}

/// <summary>
/// Contamination search, run before any freeze.
///
/// It looks in every direction a leak can come from: between splits, and against the Run-1
/// corpora the mouth's predecessor already saw. A test row that appeared in run-1a's training set
/// measures nothing.
/// </summary>
public static class Contamination
{
    public sealed record Finding(string RowId, string Where, string Detail);

    public static IReadOnlyList<Finding> Search(
        IReadOnlyList<(TrainingRow Row, TrainingRowMetadata Meta)> rows,
        IReadOnlyCollection<string> priorCorpusTargets)
    {
        var findings = new List<Finding>();

        // 1. A scenario family appearing in more than one split.
        foreach (var group in rows.GroupBy(r => r.Meta.ScenarioFamilyId, StringComparer.Ordinal))
        {
            var splits = group.Select(r => r.Meta.Split).Where(s => s is not null)
                .Distinct(StringComparer.Ordinal).ToList();
            if (splits.Count > 1)
                findings.Add(new Finding(group.Key, "split-crossing",
                    $"family spans {string.Join(", ", splits)}"));
        }

        // 2. A target identical to one the Run-1 corpora already trained on.
        var prior = new HashSet<string>(
            priorCorpusTargets.Select(Normalize), StringComparer.Ordinal);
        foreach (var (row, _) in rows)
            if (prior.Contains(Normalize(row.Target)))
                findings.Add(new Finding(row.Id, "run-1-overlap", "target appears in a Run-1 corpus"));

        // 3. The same target text in two different splits.
        foreach (var group in rows
                     .GroupBy(r => Normalize(r.Row.Target), StringComparer.Ordinal)
                     .Where(g => g.Select(r => r.Meta.Split).Distinct(StringComparer.Ordinal).Count() > 1))
            findings.Add(new Finding(group.First().Row.Id, "duplicate-across-splits",
                "identical target in more than one split"));

        return findings;
    }

    private static string Normalize(string text)
        => string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
