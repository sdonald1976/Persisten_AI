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

        var summaries = new List<DatasetSummary>();
        foreach (var (decision, rows) in corpus)
            summaries.Add(DatasetBuilder.Write(decision, rows, directory));

        Console.WriteLine($"{"decision",-22} {"rows",6}  families        train/val/test  positives");
        foreach (var s in summaries)
            Console.WriteLine(s.ToLine());

        // The incumbent's score on the same rows. This is the number a learned model has to beat,
        // and measuring it here rather than separately means the two can never drift apart.
        Console.WriteLine();
        Console.WriteLine("current heuristic on the same corpus:");
        foreach (var (decision, rows) in corpus)
        {
            var predict = Heuristic(decision);
            if (predict is null)
            {
                Console.WriteLine($"     {decision,-22} (no incumbent — nothing to beat yet)");
                continue;
            }

            var metrics = Metrics.From(decision, rows.Select(r => (r.Label, predict(r.Text))));
            Console.WriteLine("     " + metrics.ToLine());

            if (!verbose)
                continue;
            foreach (var wrong in rows.Where(r => predict(r.Text) != r.Label).Take(3))
                Console.WriteLine($"        {(wrong.Label ? "missed " : "false +")} {wrong.Text}");
        }

        Console.WriteLine();
        Console.WriteLine("splits are drawn on the TEMPLATE FAMILY, never the row: fillers make many");
        Console.WriteLine("rows one sentence, and splitting by row would score memorisation.");
        return 0;
    }

    /// <summary>The rule each decision uses today, or null where nothing implements it yet.</summary>
    private static Func<string, bool>? Heuristic(string decision) => decision switch
    {
        "memory.decision" => t => DecisionDetector.Detect(t) is not null,
        "memory.unfinished" => t => UnfinishedWorkDetector.Detect(t) is not null,
        "companion.commitment" => t => CommitmentDetector.Detect(t) is not null,
        "tool.capability" => t => ToolNudge.Detect(t) is not null,
        _ => null,
    };
}
