using System.Text.Json;

namespace Companion.Eval;

/// <summary>
/// One labelled example, in the shape a trainer wants it.
///
/// Flat and self-describing on purpose: a row that needs the generator to interpret it is a row that
/// stops being usable the moment the generator changes. Provenance travels with it for the same
/// reason — a corpus whose rows cannot say where they came from cannot be corrected later, only
/// discarded.
/// </summary>
/// <param name="Text">The input the decision is made from.</param>
/// <param name="Label">The correct answer.</param>
/// <param name="Decision">Which judgement this trains — memory.decision, tool.capability, and so on.</param>
/// <param name="Family">The template family, which is also the unit splits are drawn on.</param>
/// <param name="Difficulty">Curriculum level, so a model can be scored where it actually fails.</param>
/// <param name="Source">synthetic | weakly_labelled | human_reviewed | real_conversation.</param>
/// <param name="Generator">Generator version, so two corpora are never silently mixed.</param>
public sealed record TrainingRow(
    string Text,
    bool Label,
    string Decision,
    string Family,
    int Difficulty,
    string Source,
    string Generator);

/// <summary>
/// Turns generated cases into train/validation/test files.
///
/// The one thing it must not get wrong is leakage, and the naive split gets it wrong every time.
/// These rows come from templates crossed with fillers, so "give me a summary of the meeting" and
/// "give me a story about a lighthouse" are the same sentence twice. Splitting by ROW puts one in
/// training and the other in test, and the resulting score measures memorisation while looking like
/// generalisation — which is the most expensive kind of wrong number, because it is flattering.
///
/// So splits are drawn on the FAMILY. Every row from a template family lands in exactly one split,
/// and a model tested on an unseen family is being asked the question we actually care about: does
/// it work on a phrasing nobody wrote down for it.
/// </summary>
public static class DatasetBuilder
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Writes train/validation/test JSONL under <paramref name="directory"/> and returns a summary.
    /// Deterministic for a given seed: a corpus you cannot reproduce is one you cannot debug.
    /// </summary>
    public static DatasetSummary Write(
        string decision, IReadOnlyList<TrainingRow> rows, string directory, int seed = 1)
    {
        Directory.CreateDirectory(directory);

        var families = rows.Select(r => r.Family).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

        // Shuffled by seed rather than by hash order, so adding a family does not silently reshuffle
        // every earlier one and invalidate a comparison against yesterday's numbers.
        var rng = new Random(seed);
        var shuffled = families.OrderBy(_ => rng.Next()).ToList();

        // 60/20/20 by family. With few families the split is coarse — that is honest rather than
        // convenient, and the summary reports the counts so a corpus too small to split is visible
        // instead of quietly producing an empty test set.
        var trainCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.6));
        var validCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.2));
        var split = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < shuffled.Count; i++)
        {
            split[shuffled[i]] =
                i < trainCount ? "train"
                : i < trainCount + validCount ? "validation"
                : "test";
        }

        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in new[] { "train", "validation", "test" })
        {
            var subset = rows.Where(r => split[r.Family] == name).ToList();
            var path = Path.Combine(directory, $"{decision}.{name}.jsonl");
            using var writer = new StreamWriter(path, append: false);
            foreach (var row in subset)
                writer.WriteLine(JsonSerializer.Serialize(row, Json));
            written[name] = subset.Count;
        }

        return new DatasetSummary(
            decision,
            rows.Count,
            families.Count,
            written["train"],
            written["validation"],
            written["test"],
            rows.Count(r => r.Label),
            directory);
    }

    /// <summary>
    /// Removes rows whose text repeats, keeping the first. Templates crossed with fillers produce
    /// genuine duplicates — two families can render the same sentence — and a duplicate that lands
    /// on both sides of a split is leakage that the family rule alone will not catch.
    /// </summary>
    public static IReadOnlyList<TrainingRow> Deduplicate(IEnumerable<TrainingRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return rows.Where(r => seen.Add(r.Text.Trim())).ToList();
    }
}

/// <param name="Positives">How many rows carry the true label — a corpus at 95/5 needs saying so.</param>
public sealed record DatasetSummary(
    string Decision, int Rows, int Families, int Train, int Validation, int Test,
    int Positives, string Directory)
{
    public double PositiveRate => Rows == 0 ? 0 : Positives / (double)Rows;

    public string ToLine() =>
        $"{Decision,-22} {Rows,6} rows  {Families,3} families  " +
        $"{Train,5}/{Validation,4}/{Test,4}  {PositiveRate,5:P0} positive";
}
