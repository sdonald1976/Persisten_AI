using Companion.Core.Services;

namespace Companion.Eval;

/// <summary>
/// Builds the training corpus and, in the same pass, scores the heuristic each decision currently
/// uses against it.
///
/// The two belong together. A corpus without the incumbent's score on it is a pile of rows nobody
/// can act on — "we have 12,000 examples" says nothing about whether a model is worth training,
/// while "the current rule scores 0.71 on them" says exactly what the bar is.
/// </summary>
public static class CorpusSuite
{
    public static int Run(string directory, bool verbose)
    {
        var corpus = CognitiveCorpus.Build();
        Console.WriteLine($"corpus -> {directory}");
        Console.WriteLine();

        // Stamp each row with what the shipped rule answers for it, before writing. A trainer that
        // has to reimplement the incumbent in order to score it against the same rows is a trainer
        // whose baseline drifts; carrying the answer in the data makes drift impossible.
        var stamped = corpus.ToDictionary(
            e => e.Key,
            e => Stamp(e.Key, e.Value),
            StringComparer.Ordinal);

        var summaries = new List<DatasetSummary>();
        foreach (var (decision, rows) in stamped)
            summaries.Add(DatasetBuilder.Write(decision, rows, directory));

        Console.WriteLine($"{"decision",-22} {"rows",6}  families        train/val/test  positives");
        foreach (var s in summaries)
            Console.WriteLine(s.ToLine());

        // The incumbent's score on the same rows. This is the number a learned model has to beat,
        // and measuring it here rather than separately means the two can never drift apart.
        Console.WriteLine();
        Console.WriteLine("current heuristic on the same corpus — by family first, then by row:");
        foreach (var (decision, rows) in stamped)
        {
            if (rows.Count == 0 || rows[0].Heuristic is null)
            {
                Console.WriteLine($"     {decision,-22} (no incumbent — nothing to beat yet)");
                continue;
            }

            var byFamily = Metrics.ByFamily(
                decision, rows.Select(r => (r.Family, r.Label, r.Heuristic!.Value)));
            var byRow = Metrics.From("  ...the same, by row", rows.Select(r => (r.Label, r.Heuristic!.Value)));
            Console.WriteLine("     " + byFamily.ToLine());
            Console.WriteLine("     " + byRow.ToLine());

            if (!verbose)
                continue;
            foreach (var wrong in rows.Where(r => r.Heuristic != r.Label).Take(3))
                Console.WriteLine($"        {(wrong.Label ? "missed " : "false +")} {wrong.Text}");
        }

        // The rows that came from elsewhere. Stamped by running the same detectors, so that a
        // borrowed corpus makes the baseline harder rather than handing it fifteen thousand easy
        // negatives it was never asked about.
        BorrowedStamp.Run(directory, Heuristic);

        Console.WriteLine();
        Console.WriteLine("splits are drawn on the TEMPLATE FAMILY, never the row: fillers make many");
        Console.WriteLine("rows one sentence, and splitting by row would score memorisation.");
        Console.WriteLine("the family score is the one to read — a template with a {when} filler renders");
        Console.WriteLine("sixty rows and a bare one renders ten, so row averages weight phrasings by");
        Console.WriteLine("how many fillers someone wrote rather than by how much they matter.");
        return 0;
    }

    /// <summary>Records the incumbent's answer on every row, where there is an incumbent.</summary>
    private static IReadOnlyList<TrainingRow> Stamp(string decision, IReadOnlyList<TrainingRow> rows)
    {
        var predict = Heuristic(decision);
        return predict is null ? rows : rows.Select(r => r with { Heuristic = predict(r.Text) }).ToList();
    }

    /// <summary>The rule each decision uses today, or null where nothing implements it yet.</summary>
    private static Func<string, bool>? Heuristic(string decision) => decision switch
    {
        "memory.decision" => t => DecisionDetector.Detect(t) is not null,
        "memory.unfinished" => t => UnfinishedWorkDetector.Detect(t) is not null,
        "companion.commitment" => t => CommitmentDetector.Detect(t) is not null,
        // The CAPABILITY nudge, not "any nudge fired". ToolNudge dispatches seven different
        // lookups, and scoring all of them as this one decision made the preferences nudge
        // answering "what are your hobbies" — which is its job, correctly done — read as a
        // capability false positive. On CLINC150 that alone was 22 of the 72 recorded, so the
        // number being reported was partly a measurement artefact rather than a defect.
        "tool.capability" => t => ToolNudge.Detect(t)?.Tool == "capability.list",
        _ => null,
    };
}
