using System.Text.Json;
using System.Text.Json.Nodes;

namespace Companion.Eval;

/// <summary>
/// Runs the shipped detectors over the corpora that came from somewhere else, so the incumbent has
/// a verdict on them too.
///
/// The generated corpus carries the incumbent's answer because <see cref="CorpusSuite"/> stamps it
/// on the way past. Borrowed rows (a research corpus) and harvested rows (real conversations) are
/// written by Python and arrive with no verdict at all, and <c>crossval.py</c> then scores the
/// incumbent as if it had DECLINED on every one of them.
///
/// That is not a small distortion, and it runs the wrong way. On the first real run, 15,250 CLINC150
/// utterances entered the tool.capability comparison unstamped; almost all of them are negatives, so
/// the incumbent collected fifteen thousand free true-negatives and its precision at a 3 % base rate
/// came out 0.739 against its own 0.030 on the rows it was actually run over. A borrowed corpus is
/// supposed to make the baseline harder to beat, not hand it the easiest rows in the set.
///
/// So the fix is the one the design document already names: score the incumbent on borrowed data by
/// RUNNING it, not by assuming. This is deliberately in the eval tool rather than in an adapter,
/// because the whole point is that the comparison uses the detector that ships, not a Python
/// transcription of it that drifts.
///
/// Decisions with no single-string rule are left alone and said so. <c>memory.supersession</c> and
/// <c>memory.assertion</c> are pair judgements that need more than a sentence — inventing a
/// plausible one-argument stand-in for them would produce exactly the fake baseline this exists to
/// remove.
/// </summary>
public static class BorrowedStamp
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The suffixes written from outside the generator, and therefore never stamped.</summary>
    private static readonly string[] Suffixes = [".borrowed.jsonl", ".reviewed.jsonl", ".captured.jsonl"];

    public static void Run(string directory, Func<string, Func<string, bool>?> heuristicFor)
    {
        if (!Directory.Exists(directory))
            return;

        var files = Suffixes
            .SelectMany(suffix => Directory.EnumerateFiles(directory, $"*{suffix}")
                .Select(path => (Path: path, Decision: Path.GetFileName(path)[..^suffix.Length])))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("borrowed and harvested corpora — the incumbent RUN over them, not assumed:");

        foreach (var (path, decision) in files)
        {
            var predict = heuristicFor(decision);
            if (predict is null)
            {
                Console.WriteLine($"     {Path.GetFileName(path),-42} no single-sentence rule for "
                                  + $"{decision}; left unstamped");
                continue;
            }

            var rows = new List<JsonObject>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (JsonNode.Parse(line) is JsonObject row)
                    rows.Add(row);
            }

            if (rows.Count == 0)
            {
                Console.WriteLine($"     {Path.GetFileName(path),-42} empty");
                continue;
            }

            // Parsed as a JsonObject rather than into TrainingRow so that every field the Python
            // side wrote survives the round trip. A stamping pass that silently drops a column it
            // does not know about is a stamping pass that quietly rewrites the corpus.
            var scored = new List<(bool Label, bool Said)>(rows.Count);
            foreach (var row in rows)
            {
                var text = row["text"]?.GetValue<string>() ?? string.Empty;
                var said = predict(text);
                row["heuristic"] = said;

                // A review queue carries label: null until a human fills it in. Those rows can be
                // stamped — the incumbent's answer is exactly what sorts the queue — but they
                // cannot be scored, because there is nothing yet to be right or wrong about.
                if (row["label"] is JsonValue value && value.TryGetValue<bool>(out var label))
                    scored.Add((label, said));
            }

            var temp = path + ".tmp";
            using (var writer = new StreamWriter(temp, append: false))
                foreach (var row in rows)
                    writer.WriteLine(row.ToJsonString(Json));
            File.Move(temp, path, overwrite: true);

            if (scored.Count == 0)
            {
                Console.WriteLine($"     {Path.GetFileName(path),-42} {rows.Count,6} rows stamped, "
                                  + "none labelled yet (a review queue)");
                continue;
            }

            var byRow = Metrics.From(Path.GetFileName(path), scored);
            Console.WriteLine("     " + byRow.ToLine());
        }

        Console.WriteLine();
        Console.WriteLine("an incumbent scored as declining on rows it was never run over collects free");
        Console.WriteLine("true-negatives and reports a precision it has not earned — which is what the");
        Console.WriteLine("borrowed rows did to tool.capability before this pass existed.");
    }
}
