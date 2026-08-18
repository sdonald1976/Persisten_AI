using System.Text.Json;
using System.Text.Json.Nodes;
using Companion.Core.Services;

namespace Companion.Eval;

/// <summary>
/// Runs the shipped supersession rules over pair-task rows, so the incumbent has a verdict on the
/// corpus the specialised model trains on — produced by the code that ships, not a transcription.
///
/// The incumbent here is the deterministic core of <c>MemoryPipeline.ProcessSemanticAsync</c>:
/// value-key identity (duplicate), predicate cardinality (single-valued displaces), the
/// replacement-phrase signal over the user's words, and the mention check. The similarity floor is
/// deliberately absent — it needs the embedding model, and its job in production is only to stop a
/// replacement landing on something unrelated, which pair rows have already decided by existing.
///
/// Its label space is a strict subset of the task's: the incumbent can say DUPLICATE, SUPERSEDES or
/// COEXIST, and can never say CORRECTS, REFINES, CONTRADICTS or UNCERTAIN — it has no concept of
/// them. That is not a scoring problem, it is the point: the classes the incumbent cannot express
/// are the part of the decision that is currently not being made at all. Scoring happens at the
/// ACTION level (does the store supersede, confirm, or add), where both vocabularies are complete.
/// </summary>
public static class PairStamp
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The labels whose production action is a supersede — the destructive direction.</summary>
    private static readonly HashSet<string> SupersedeActions =
        new(StringComparer.Ordinal) { "SUPERSEDES", "CORRECTS" };

    public static void Run(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        var files = Directory.EnumerateFiles(directory, "memory.supersession.pair.*.jsonl")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("supersession pairs — the shipped rules RUN over each pair, action-level score beside:");

        foreach (var path in files)
        {
            var rows = File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonNode.Parse(line) as JsonObject)
                .Where(row => row is not null)
                .Select(row => row!)
                .ToList();
            if (rows.Count == 0)
                continue;

            var scored = new List<(bool Expected, bool Actual)>();
            foreach (var row in rows)
            {
                var verdict = Incumbent(row);
                row["incumbent"] = verdict;

                var label = row["label"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(label) && label != "UNCERTAIN")
                    scored.Add((SupersedeActions.Contains(label), verdict == "SUPERSEDES"));
            }

            var temp = path + ".tmp";
            using (var writer = new StreamWriter(temp, append: false))
                foreach (var row in rows)
                    writer.WriteLine(row.ToJsonString(Json));
            File.Move(temp, path, overwrite: true);

            if (scored.Count == 0)
            {
                Console.WriteLine($"     {Path.GetFileName(path),-46} {rows.Count,6} rows stamped, none labelled");
                continue;
            }

            // The supersede action is the one that buries a fact, so its precision/recall is the
            // budget every migration criterion is written against. UNCERTAIN rows are excluded:
            // a genuinely ambiguous pair has no right action to be scored on.
            Console.WriteLine("     " + Metrics.From(
                $"{Path.GetFileName(path)} (supersede action)", scored).ToLine());
        }
    }

    /// <summary>
    /// What the shipped code decides for this pair, using the real detectors. Order mirrors the
    /// pipeline: identity first, cardinality second, wording+mention third, join otherwise.
    /// </summary>
    private static string Incumbent(JsonObject row)
    {
        var incoming = row["incoming"]!.AsObject();
        var existing = row["existing"]!.AsObject();
        var pair = row["pair"]?.AsObject();

        var predicate = incoming["predicate"]?.GetValue<string>();
        var incomingValue = incoming["value"]?.GetValue<string>();
        var existingValue = existing["value"]?.GetValue<string>();
        var utterance = incoming["utterance"]?.GetValue<string>() ?? "";
        var sameSlot = pair?["same_slot"]?.GetValue<bool>()
            ?? string.Equals(predicate, existing["predicate"]?.GetValue<string>(), StringComparison.Ordinal);

        // The exact-restatement check the pipeline runs first, on the same normalized key.
        if (sameSlot && MemoryNormalizer.SemanticValueKey("user", predicate, incomingValue)
                == MemoryNormalizer.SemanticValueKey("user", existing["predicate"]?.GetValue<string>(), existingValue))
            return "DUPLICATE";

        if (sameSlot && FactSupersession.IsSingleValued(predicate))
            return "SUPERSEDES";

        // Wording plus the mention check — the same pair of guards the pipeline applies, with the
        // same inputs: the user's words and the candidate value against the existing value.
        var mentions = ScoreMath.KeywordOverlap($"{incomingValue} {utterance}", existingValue) > 0;
        if (sameSlot && FactSupersession.SignalsReplacement(new[] { utterance }) && mentions)
            return "SUPERSEDES";

        return "COEXIST";
    }
}
