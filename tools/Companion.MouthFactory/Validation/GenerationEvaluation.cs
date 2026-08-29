using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>One arm's score on one split.</summary>
public sealed record ArmScore
{
    public required string Arm { get; init; }
    public required string Split { get; init; }
    public required int Rows { get; init; }

    /// <summary>Rows passing every deterministic gate. The headline fidelity number.</summary>
    public required int Clean { get; init; }

    /// <summary>Failures per check, so a regression can be attributed rather than guessed at.</summary>
    public required IReadOnlyDictionary<string, int> Failures { get; init; }

    /// <summary>Per-family clean rate, so a gain in one stratum cannot hide a loss in another.</summary>
    public required IReadOnlyDictionary<string, (int Clean, int Rows)> ByFamily { get; init; }

    /// <summary>Distinct openings over rows: the over-specialisation measure Run-1 also used.</summary>
    public required double OpeningDiversity { get; init; }

    /// <summary>Distinct replies over rows. A model that has collapsed says the same thing.</summary>
    public required double DistinctReplies { get; init; }

    public required int MedianWords { get; init; }

    public double CleanRate => Rows == 0 ? 0 : Clean / (double)Rows;
}

/// <summary>
/// Scores generated replies against the scenario truth they were generated from.
///
/// The instrument is <see cref="DeterministicChecks"/> — the same gate the corpus was built and
/// frozen against, unchanged. That matters more than it sounds: an evaluation written separately
/// from the thing it evaluates measures the difference between two implementations as readily as
/// it measures the model. Here there is only one implementation, so "did the reply obey the plan"
/// means exactly what it meant when the row was accepted.
///
/// Nothing here consults a model. Naturalness is the one dimension that genuinely needs a reader,
/// and it is reported from the critic verdicts already stored on the corpus rather than by asking
/// a judge a second time about a different text.
/// </summary>
public static class GenerationEvaluation
{
    public sealed record Generation(string Id, string Target);

    public static ArmScore Score(
        string arm, string split,
        IReadOnlyList<Generation> generations,
        IReadOnlyDictionary<string, TrainingRowMetadata> metadata,
        IReadOnlyDictionary<string, ScenarioTruth> scenarios)
    {
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var byFamily = new Dictionary<string, (int Clean, int Rows)>(StringComparer.Ordinal);
        var openings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<int>();
        var clean = 0;
        var scored = 0;

        foreach (var generation in generations)
        {
            if (!metadata.TryGetValue(generation.Id, out var meta)
                || !scenarios.TryGetValue(meta.ScenarioId, out var scenario))
                continue;

            scored++;
            var checks = DeterministicChecks.Run(scenario, generation.Target);
            var failed = checks.Where(c => !c.Passed).ToList();
            foreach (var f in failed)
                failures[f.Name] = failures.GetValueOrDefault(f.Name) + 1;

            var ok = failed.Count == 0;
            if (ok)
                clean++;

            var family = meta.FamilyId;
            var (fc, fr) = byFamily.GetValueOrDefault(family);
            byFamily[family] = (fc + (ok ? 1 : 0), fr + 1);

            openings.Add(RowRendering.Opening(generation.Target));
            replies.Add(Normalise(generation.Target));
            words.Add(generation.Target.Split(
                [' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length);
        }

        words.Sort();
        return new ArmScore
        {
            Arm = arm,
            Split = split,
            Rows = scored,
            Clean = clean,
            Failures = failures,
            ByFamily = byFamily,
            OpeningDiversity = scored == 0 ? 0 : openings.Count / (double)scored,
            DistinctReplies = scored == 0 ? 0 : replies.Count / (double)scored,
            MedianWords = words.Count == 0 ? 0 : words[words.Count / 2],
        };
    }

    private static string Normalise(string text)
        => string.Join(' ', text.ToLowerInvariant()
            .Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
