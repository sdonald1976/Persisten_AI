using System.Text.Json;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Companion.Infrastructure.Cognition;
using Microsoft.Extensions.Logging.Abstractions;

namespace Companion.Eval;

/// <summary>
/// Head-to-head on the judgment nine subsystems share: given a reference and a set of candidates,
/// which one does the user mean?
///
/// <c>ScoreMath.KeywordOverlap</c> is the incumbent — Jaccard over a fifty-word stopword list and a
/// five-rule stemmer — and it decides which project a reference resolves to, which memory
/// "that's wrong" flags, which procedure matches, and six other things. The cases it cannot do
/// anything about are the ones with no shared vocabulary at all: "the buoy one" and "Marsh Lane
/// marine sensor project" overlap in nothing, so its score is exactly zero and the tie is broken by
/// whatever happened to be first.
///
/// A cross-encoder reads the pair together and can. Whether it actually does, on THIS data, is what
/// this measures — the rule being that a model replaces a heuristic on evidence, not reputation.
/// </summary>
public static class Ranking
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (RankingMetrics Keyword, RankingMetrics? Model, RankingMetrics? Hybrid, double MsPerQuery) Run(
        string path, string? modelDirectory, bool verbose)
    {
        var rows = Load(path);
        if (rows.Count == 0)
            return (RankingMetrics.From("keyword", Array.Empty<int>()), null, null, 0);

        var keywordScores = new List<double[]>();
        var keywordRanks = new List<int>();
        foreach (var row in rows)
        {
            var scores = row.Candidates.Select(c => ScoreMath.KeywordOverlap(row.Reference, c)).ToArray();
            keywordScores.Add(scores);

            var ranked = Order(scores);
            var rank = RankOf(ranked, row.Correct);
            keywordRanks.Add(rank);

            if (verbose && rank != 1)
                Console.WriteLine($"     keyword missed: \"{row.Reference}\" → \"{row.Candidates[ranked[0]]}\" (wanted \"{row.Candidates[row.Correct]}\")");
        }

        var keyword = RankingMetrics.From("keyword-overlap", keywordRanks);
        if (modelDirectory is null)
            return (keyword, null, null, 0);

        var scorer = new OnnxTextPairScorer(
            "reranker",
            new CognitiveModelEntry { Enabled = true, Path = "reranker.onnx", MaxTokens = 128 },
            modelDirectory,
            TimeSpan.FromSeconds(30),
            NullLogger<OnnxTextPairScorer>.Instance);

        if (!scorer.IsAvailable)
        {
            Console.WriteLine($"     (no reranker: {scorer.Status.Reason})");
            return (keyword, null, null, 0);
        }

        var modelRanks = new List<int>();
        var hybridRanks = new List<int>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var scores = scorer.ScoreAsync(row.Reference, row.Candidates).GetAwaiter().GetResult();
            if (scores.Count != row.Candidates.Count)
            {
                Console.WriteLine("     (reranker returned nothing; falling back)");
                return (keyword, null, null, 0);
            }

            var ranked = Order(scores);
            var rank = RankOf(ranked, row.Correct);
            modelRanks.Add(rank);

            if (verbose && rank != 1)
                Console.WriteLine($"     model missed:   \"{row.Reference}\" → \"{row.Candidates[ranked[0]]}\" (wanted \"{row.Candidates[row.Correct]}\")");

            // Both signals, each normalized within this candidate set before adding. Normalizing
            // per query is not a nicety: cross-encoder logits are on the model's own scale and
            // Jaccard is bounded 0..1, so summing them raw would let one drown the other for
            // reasons that have nothing to do with either being right.
            var combined = Normalize(scores).Zip(Normalize(keywordScores[r]), (m, k) => m + k).ToArray();
            var hybridRanked = Order(combined);
            var hybridRank = RankOf(hybridRanked, row.Correct);
            hybridRanks.Add(hybridRank);

            if (verbose && hybridRank != 1)
                Console.WriteLine($"     hybrid missed:  \"{row.Reference}\" → \"{row.Candidates[hybridRanked[0]]}\" (wanted \"{row.Candidates[row.Correct]}\")");
        }
        started.Stop();

        return (
            keyword,
            RankingMetrics.From("cross-encoder", modelRanks),
            RankingMetrics.From("hybrid", hybridRanks),
            started.Elapsed.TotalMilliseconds / rows.Count);
    }

    /// <summary>Candidate indices, best first.</summary>
    private static int[] Order(IReadOnlyList<double> scores)
        => scores
            .Select((s, i) => (Index: i, Score: s))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Index)
            .ToArray();

    /// <summary>Min-max to 0..1 within one candidate set, so two different scales can be added.</summary>
    private static double[] Normalize(IReadOnlyList<double> scores)
    {
        if (scores.Count == 0)
            return Array.Empty<double>();
        var min = scores.Min();
        var range = scores.Max() - min;
        return range <= 1e-9
            ? scores.Select(_ => 0.0).ToArray()   // all equal: this signal has no opinion here
            : scores.Select(s => (s - min) / range).ToArray();
    }

    /// <summary>Where the correct candidate landed, 1-based; 0 when it never appeared.</summary>
    private static int RankOf(IEnumerable<int> order, int correct)
    {
        var rank = 1;
        foreach (var index in order)
        {
            if (index == correct)
                return rank;
            rank++;
        }
        return 0;
    }

    private static IReadOnlyList<RankingRow> Load(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<RankingRow>();

        var rows = new List<RankingRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            var row = JsonSerializer.Deserialize<RankingRow>(line, Json);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }
}

/// <summary>One reference, the candidates it could mean, and which one it does.</summary>
public sealed record RankingRow(
    string Reference,
    IReadOnlyList<string> Candidates,
    int Correct,
    string? Source);
