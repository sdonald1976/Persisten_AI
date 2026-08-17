using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// A cross-encoder: reads a query and a candidate <em>together</em> and scores how well the second
/// answers the first.
///
/// That togetherness is the whole point, and it is what neither of the two things it replaces can
/// do. An embedding encodes each text alone and compares the results, so it measures similarity and
/// not relevance. <c>ScoreMath.KeywordOverlap</c> is Jaccard over a stopword list and a five-rule
/// stemmer, so it cannot see that "the buoy one" refers to a marine sensor project — there are no
/// shared tokens to overlap. A cross-encoder attends across the pair and can.
///
/// Scores are logits from the model's own scale. They rank a candidate set and mean nothing across
/// different queries: rank with them, never threshold globally.
/// </summary>
internal sealed class OnnxTextPairScorer : ITextPairScorer, IDisposable
{
    private readonly OnnxSession _session;
    private readonly BertLikeTokenizer _tokenizer;
    private readonly int _maxTokens;

    public OnnxTextPairScorer(
        string name, CognitiveModelEntry entry, string directory, TimeSpan timeout, ILogger logger)
    {
        Name = name;
        _maxTokens = Math.Max(16, entry.MaxTokens);
        _session = OnnxSession.Load(name, entry, directory, timeout, logger);
        _tokenizer = BertLikeTokenizer.Load(entry, directory, logger);
    }

    public string Name { get; }

    /// <summary>
    /// Both halves have to be there. A loaded graph with no vocabulary cannot turn text into ids,
    /// and reporting available on the strength of the file existing would fall back at the worst
    /// possible moment — mid-turn, per call, instead of once at startup where it is visible.
    /// </summary>
    public bool IsAvailable => _session.IsAvailable && _tokenizer.IsAvailable;

    public CognitiveModelStatus Status => _session.Status with
    {
        Available = IsAvailable,
        Reason = _session.IsAvailable && !_tokenizer.IsAvailable ? _tokenizer.Reason : _session.Reason,
    };

    public async Task<IReadOnlyList<double>> ScoreAsync(
        string query, IReadOnlyList<string> candidates, CancellationToken ct = default)
    {
        if (!IsAvailable || candidates.Count == 0)
            return Array.Empty<double>();

        var scores = new double[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var score = await ScoreOneAsync(query, candidates[i], ct);
            // One candidate failing must not silently reorder the rest around it, so a partial
            // result is no result: the caller falls back to the heuristic for the whole set.
            if (score is null)
                return Array.Empty<double>();
            scores[i] = score.Value;
        }
        return scores;
    }

    private async Task<double?> ScoreOneAsync(string query, string candidate, CancellationToken ct)
    {
        var encoded = _tokenizer.EncodePair(query, candidate, _maxTokens);
        var length = encoded.InputIds.Length;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", Tensor(encoded.InputIds, length)),
            NamedOnnxValue.CreateFromTensor("attention_mask", Tensor(encoded.AttentionMask, length)),
        };
        // Some exports drop token_type_ids; sending an input the graph doesn't declare is an error,
        // so it is added only when the model asks for it.
        if (_session.InputNames.Contains("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", Tensor(encoded.TokenTypeIds, length)));

        var output = await _session.RunAsync(inputs, ct);
        if (output is null || output.Length == 0)
            return null;

        // A single logit is already a relevance score. Two are [irrelevant, relevant] and the
        // margin between them is the score — softmax would be a monotonic transform of the same
        // ordering, and ordering is all these are used for.
        return output.Length == 1 ? output[0] : output[1] - output[0];
    }

    private static DenseTensor<long> Tensor(long[] values, int length)
        => new(values, new[] { 1, length });

    public void Dispose() => _session.Dispose();
}
