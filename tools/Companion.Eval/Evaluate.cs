using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Companion.Infrastructure.Cognition;
using Microsoft.Extensions.Logging.Abstractions;

namespace Companion.Eval;

/// <summary>
/// Runs each labelled dataset through the real heuristic — not a copy of it. The harness references
/// <c>Companion.Core</c> on purpose: a reimplementation here would drift from the shipped code and
/// end up flattering whatever replaced it.
/// </summary>
public static class Evaluate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><see cref="DecisionDetector"/>: did the user actually settle something?</summary>
    public static Metrics Decision(string path, bool verbose)
        => Score("decision", Load<LabelledText>(path), verbose,
            row => DecisionDetector.Detect(row.Text) is not null,
            row => row.Label,
            row => row.Text);

    /// <summary><see cref="AssertionGuard"/>: did the user state this, or merely say the words?</summary>
    public static Metrics Assertion(string path, bool verbose)
        => Score("assertion", Load<LabelledExcerpt>(path), verbose,
            row => AssertionGuard.IsAsserted(row.Excerpt, row.Text),
            row => row.Label,
            row => $"\"{row.Excerpt}\" in \"{row.Text}\"");

    /// <summary>
    /// <see cref="FactSupersession"/>: does the incoming fact replace the existing one, or join it?
    ///
    /// Scored on the two signals the pipeline actually uses — predicate cardinality is not
    /// available from text alone, so this measures the wording signal, which is the half a model
    /// would replace. The half it would not replace is the cardinality table, and that is code.
    /// </summary>
    public static Metrics Supersession(string path, bool verbose)
        => Score("supersession", Load<LabelledPair>(path), verbose,
            row => FactSupersession.SignalsReplacement(new[] { row.Said }),
            row => row.Label,
            row => $"{row.Existing} ← {row.Said}");

    /// <summary>
    /// The same judgment, asked of an NLI model instead: does the existing memory <em>contradict</em>
    /// the incoming one? This is the head-to-head that decides whether entailment is worth its
    /// inference cost on the supersession path, and it is the case embedding similarity is
    /// structurally unable to make — 0.763 for the pair that must replace, 0.753 for the pair that
    /// must coexist.
    /// </summary>
    public static Metrics? SupersessionNli(string path, string? modelDirectory, bool verbose, out double msPerCall)
    {
        msPerCall = 0;
        if (modelDirectory is null)
            return null;

        var nli = new OnnxNliModel(
            "nli",
            new CognitiveModelEntry { Enabled = true, Path = "nli.onnx", MaxTokens = 128 },
            modelDirectory,
            TimeSpan.FromSeconds(30),
            NullLogger.Instance);

        if (!nli.IsAvailable)
        {
            Console.WriteLine($"     (no nli model: {nli.Status.Reason})");
            return null;
        }

        var rows = Load<LabelledPair>(path);
        var results = new List<(bool, bool)>(rows.Count);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var row in rows)
        {
            // Premise: what we already hold. Hypothesis: what has just arrived. A contradiction
            // between them is what "this replaces that" actually means.
            var verdict = nli.ClassifyAsync(row.Existing, row.Incoming).GetAwaiter().GetResult();
            var predicted = verdict.Label == Entailment.Contradiction;
            results.Add((row.Label, predicted));

            if (verbose && row.Label != predicted)
            {
                Console.WriteLine(
                    $"     {(row.Label ? "missed" : "false +")}: \"{row.Existing}\" vs \"{row.Incoming}\" " +
                    $"→ {verdict.Label} {verdict.Confidence:P0}");
            }
        }

        watch.Stop();
        msPerCall = rows.Count == 0 ? 0 : watch.Elapsed.TotalMilliseconds / rows.Count;
        return Metrics.From("supersession-nli", results);
    }

    private static Metrics Score<T>(
        string name,
        IReadOnlyList<T> rows,
        bool verbose,
        Func<T, bool> predict,
        Func<T, bool> expected,
        Func<T, string> describe)
    {
        var results = new List<(bool Expected, bool Actual)>(rows.Count);
        foreach (var row in rows)
        {
            var want = expected(row);
            var got = predict(row);
            results.Add((want, got));

            // Only the mistakes, and only on request: a list of everything it got right is the
            // fastest way to stop reading the output at all.
            if (verbose && want != got)
                Console.WriteLine($"     {(want ? "missed" : "false +")}: {describe(row)}");
        }
        return Metrics.From(name, results);
    }

    private static IReadOnlyList<T> Load<T>(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<T>();

        var rows = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            var row = JsonSerializer.Deserialize<T>(line, Json);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }
}

/// <summary>
/// Where a labelled row came from, because they are not equally trustworthy and training on the
/// wrong ones is how a model learns to reproduce the heuristic's mistakes instead of fixing them.
/// </summary>
public enum LabelSource
{
    /// <summary>Generated from templates. Cheap, plentiful, and only as good as the templates.</summary>
    Synthetic,

    /// <summary>Labelled by the existing heuristic. Useful volume, inherits every bias it has.</summary>
    WeaklyLabelled,

    /// <summary>A person decided. The only kind worth resolving a disagreement with.</summary>
    HumanReviewed,

    /// <summary>Taken verbatim from a conversation that actually happened.</summary>
    RealConversation,

    /// <summary>Deliberately shares vocabulary with the opposite label — the cases that separate a model from a keyword list.</summary>
    HardNegative,
}

public sealed record LabelledText(string Text, bool Label, [property: JsonPropertyName("source")] string? Source);

public sealed record LabelledExcerpt(
    string Text, string Excerpt, bool Label, [property: JsonPropertyName("source")] string? Source);

public sealed record LabelledPair(
    string Existing, string Incoming, string Said, bool Label,
    [property: JsonPropertyName("source")] string? Source);
