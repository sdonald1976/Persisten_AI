using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Reads the emotional tone of a single user message with a deterministic, offline lexicon — the
/// same conservative spirit as <see cref="CommitmentDetector"/>: it only fires on a clear emotional
/// cue and stays quiet (returns <see cref="MoodReading.Neutral"/>) on flat, ordinary text, so the
/// relational layer accumulates real signal rather than noise. Handles simple negation ("not great"
/// → mildly negative, "not stressed" → mildly positive) and intensifiers ("really stressed" →
/// strongly negative). No model call, no I/O — trivially unit-testable.
/// </summary>
public static partial class MoodDetector
{
    // Signed valence per cue word: negative = distressed, positive = upbeat. Magnitudes are coarse
    // by design (three bands each way) — the point is direction and rough strength, not precision.
    private static readonly IReadOnlyDictionary<string, double> Lexicon = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        // strong negative
        ["devastated"] = -0.9, ["miserable"] = -0.9, ["hopeless"] = -0.9, ["depressed"] = -0.9,
        ["awful"] = -0.85, ["terrible"] = -0.85, ["horrible"] = -0.85, ["furious"] = -0.85,
        ["heartbroken"] = -0.9, ["dreadful"] = -0.85, ["worst"] = -0.8, ["hate"] = -0.8,
        // mid negative
        ["stressed"] = -0.6, ["overwhelmed"] = -0.65, ["anxious"] = -0.6, ["worried"] = -0.6,
        ["frustrated"] = -0.6, ["angry"] = -0.6, ["upset"] = -0.6, ["sad"] = -0.6,
        ["exhausted"] = -0.6, ["nervous"] = -0.55, ["scared"] = -0.6, ["afraid"] = -0.6,
        ["lonely"] = -0.65, ["hurt"] = -0.55, ["drained"] = -0.6, ["sucks"] = -0.6,
        ["worse"] = -0.6, ["bad"] = -0.5, ["crap"] = -0.5,
        // mild negative
        ["tired"] = -0.35, ["annoyed"] = -0.35, ["bored"] = -0.35, ["meh"] = -0.35,
        ["disappointed"] = -0.4, ["unsure"] = -0.3, ["confused"] = -0.3, ["down"] = -0.4,
        ["stuck"] = -0.35, ["nervy"] = -0.35,
        // mild positive
        ["okay"] = 0.3, ["ok"] = 0.3, ["fine"] = 0.3, ["alright"] = 0.3, ["better"] = 0.4,
        ["calm"] = 0.35, ["content"] = 0.4, ["decent"] = 0.3, ["nice"] = 0.4,
        // mid positive
        ["happy"] = 0.6, ["glad"] = 0.6, ["great"] = 0.6, ["excited"] = 0.65, ["grateful"] = 0.6,
        ["relieved"] = 0.6, ["proud"] = 0.6, ["hopeful"] = 0.55, ["love"] = 0.7, ["joy"] = 0.65,
        ["pleased"] = 0.55, ["good"] = 0.35, ["stoked"] = 0.7, ["psyched"] = 0.7,
        // strong positive
        ["thrilled"] = 0.9, ["ecstatic"] = 0.9, ["overjoyed"] = 0.9, ["amazing"] = 0.85,
        ["fantastic"] = 0.85, ["wonderful"] = 0.85, ["delighted"] = 0.85, ["elated"] = 0.9,
        ["awesome"] = 0.8, ["incredible"] = 0.8,
    };

    private static readonly HashSet<string> Intensifiers = new(StringComparer.Ordinal)
    {
        "really", "very", "so", "super", "extremely", "incredibly", "quite", "totally",
        "absolutely", "deeply", "terribly", "insanely", "ridiculously", "damn",
    };

    private static readonly HashSet<string> Negators = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "isn't", "wasn't", "aren't", "weren't", "don't", "doesn't",
        "didn't", "can't", "cannot", "won't", "wouldn't", "ain't", "hardly", "barely", "n't",
    };

    [GeneratedRegex(@"[a-z']+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static MoodReading Detect(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return MoodReading.Neutral;

        var tokens = Words().Matches(message.ToLowerInvariant())
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0)
            return MoodReading.Neutral;

        var bestValence = 0.0;
        string? bestLabel = null;
        string? bestEvidence = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (!Lexicon.TryGetValue(tokens[i], out var valence))
                continue;

            var negated = false;
            var intensified = false;

            // Look back a few tokens for a negator or intensifier modifying this cue.
            for (var j = i - 1; j >= 0 && j >= i - 3; j--)
            {
                if (Negators.Contains(tokens[j]))
                    negated = true;
                if (j >= i - 2 && Intensifiers.Contains(tokens[j]))
                    intensified = true;
            }

            if (intensified)
                valence *= 1.5;
            if (negated)
                valence *= -0.6; // "not great" → mildly negative; "not stressed" → mildly positive

            valence = Math.Clamp(valence, -1.0, 1.0);

            // The most salient emotion (largest magnitude) drives the reading.
            if (Math.Abs(valence) > Math.Abs(bestValence))
            {
                bestValence = valence;
                bestLabel = tokens[i];
                bestEvidence = negated ? $"not {tokens[i]}" : tokens[i];
            }
        }

        if (bestLabel is null)
            return MoodReading.Neutral;

        // A negated cue no longer means the cue's emotion (a "not stressed" user isn't "stressed"),
        // so drop the label but keep the valence direction as the overall read.
        var label = bestEvidence is not null && bestEvidence.StartsWith("not ", StringComparison.Ordinal)
            ? null
            : bestLabel;

        return new MoodReading(Bucket(bestValence), Math.Round(bestValence, 3), label, bestEvidence);
    }

    private static Sentiment Bucket(double valence) => valence switch
    {
        <= -0.66 => Sentiment.VeryNegative,
        <= -0.20 => Sentiment.Negative,
        < 0.20 => Sentiment.Neutral,
        < 0.66 => Sentiment.Positive,
        _ => Sentiment.VeryPositive,
    };
}
