namespace Companion.Eval;

/// <summary>
/// How a binary judgment scored against labelled data.
///
/// Accuracy is reported but deliberately not led with, because it is the number most likely to
/// mislead here. Most of these judgments are rare events — most sentences are not decisions, are
/// not corrections, do not contain unfinished work — so a detector that answers "no" to everything
/// scores 90%+ accuracy while being completely useless. Precision and recall separate "it is right
/// when it fires" from "it fires when it should", and those two failures cost different things: a
/// missed memory is an inconvenience, a fabricated one is a lie the user has to notice.
/// </summary>
public sealed record Metrics
{
    public required string Name { get; init; }
    public required int TruePositives { get; init; }
    public required int FalsePositives { get; init; }
    public required int TrueNegatives { get; init; }
    public required int FalseNegatives { get; init; }

    public int Total => TruePositives + FalsePositives + TrueNegatives + FalseNegatives;

    /// <summary>Of the times it said yes, how often it was right.</summary>
    public double Precision => Divide(TruePositives, TruePositives + FalsePositives);

    /// <summary>Of the times it should have said yes, how often it did.</summary>
    public double Recall => Divide(TruePositives, TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    /// <summary>How often it fired on something it shouldn't have — the expensive mistake here.</summary>
    public double FalsePositiveRate => Divide(FalsePositives, FalsePositives + TrueNegatives);

    public double FalseNegativeRate => Divide(FalseNegatives, FalseNegatives + TruePositives);

    public double Accuracy => Divide(TruePositives + TrueNegatives, Total);

    public static Metrics From(string name, IEnumerable<(bool Expected, bool Actual)> results)
    {
        int tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var (expected, actual) in results)
        {
            if (expected && actual) tp++;
            else if (!expected && actual) fp++;
            else if (!expected && !actual) tn++;
            else fn++;
        }
        return new Metrics
        {
            Name = name,
            TruePositives = tp,
            FalsePositives = fp,
            TrueNegatives = tn,
            FalseNegatives = fn,
        };
    }

    public string ToLine() =>
        $"{Name,-22} n={Total,-4} P={Precision:F3} R={Recall:F3} F1={F1:F3} " +
        $"FP={FalsePositives,-3} FN={FalseNegatives,-3} (acc {Accuracy:P0})";

    private static double Divide(int numerator, int denominator)
        => denominator == 0 ? 0 : numerator / (double)denominator;
}

/// <summary>
/// Ranking quality, for the judgments that pick one thing out of several — which project a
/// reference means, which memory "that's wrong" is about, which memories belong in the packet.
///
/// Precision and recall don't describe these: getting the right answer second is not the same
/// failure as not having it at all, and both matter differently again from getting it first.
/// </summary>
public sealed record RankingMetrics
{
    public required string Name { get; init; }
    public required int Queries { get; init; }
    public required int TopOneCorrect { get; init; }
    public required int TopThreeCorrect { get; init; }
    public required double MeanReciprocalRank { get; init; }

    public double Precision1 => Queries == 0 ? 0 : TopOneCorrect / (double)Queries;
    public double Recall3 => Queries == 0 ? 0 : TopThreeCorrect / (double)Queries;

    /// <summary>
    /// Builds from the rank the correct answer landed at, 1-based; 0 means it wasn't returned.
    /// </summary>
    public static RankingMetrics From(string name, IReadOnlyList<int> ranks)
    {
        var top1 = ranks.Count(r => r == 1);
        var top3 = ranks.Count(r => r is >= 1 and <= 3);
        var mrr = ranks.Count == 0 ? 0 : ranks.Average(r => r >= 1 ? 1.0 / r : 0.0);
        return new RankingMetrics
        {
            Name = name,
            Queries = ranks.Count,
            TopOneCorrect = top1,
            TopThreeCorrect = top3,
            MeanReciprocalRank = mrr,
        };
    }

    public string ToLine() =>
        $"{Name,-22} n={Queries,-4} P@1={Precision1:F3} R@3={Recall3:F3} MRR={MeanReciprocalRank:F3}";
}
