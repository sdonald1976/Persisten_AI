using System.Text.Json;
using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Natural-language inference over a premise and a hypothesis: does the first support the second,
/// contradict it, or say nothing about it?
///
/// This is the judgment the memory pipeline could not make. Supersession has to tell a changed fact
/// from an added one, and measured on this project's embedding model the change that MUST replace
/// (coffee → oat milk lattes, 0.763) scores below two dislikes that MUST coexist (0.753). No
/// threshold exists in that ordering because similarity is the wrong question; contradiction is the
/// right one, and it is what this answers.
///
/// The label order is read from the model's own config, never assumed. NLI models genuinely
/// disagree: this one is contradiction/entailment/neutral, the DeBERTa family is
/// entailment/neutral/contradiction. Guessing would invert the verdict on the one judgment that
/// retires something the user told us, and it would look like a working model doing it.
///
/// It proposes. <c>MemoryCurator</c> still decides, still requires evidence from the user, and
/// still keeps the old value as linked history.
/// </summary>
internal sealed class OnnxNliModel : INliModel, IDisposable
{
    private readonly OnnxSession _session;
    private readonly RobertaLikeTokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly Entailment[] _labels;
    private readonly string? _labelReason;

    public OnnxNliModel(
        string name, CognitiveModelEntry entry, string directory, TimeSpan timeout, ILogger logger)
    {
        Name = name;
        _maxTokens = Math.Max(16, entry.MaxTokens);
        _session = OnnxSession.Load(name, entry, directory, timeout, logger);
        _tokenizer = RobertaLikeTokenizer.Load(entry, directory, logger);
        (_labels, _labelReason) = LoadLabels(entry, directory, logger);
    }

    public string Name { get; }

    public bool IsAvailable => _session.IsAvailable && _tokenizer.IsAvailable && _labels.Length > 0;

    public CognitiveModelStatus Status => _session.Status with
    {
        Available = IsAvailable,
        Reason = FirstProblem(),
    };

    private string? FirstProblem()
    {
        if (!_session.IsAvailable)
            return _session.Reason;
        if (!_tokenizer.IsAvailable)
            return _tokenizer.Reason;
        return _labels.Length == 0 ? _labelReason : null;
    }

    public async Task<EntailmentVerdict> ClassifyAsync(
        string premise, string hypothesis, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(premise) || string.IsNullOrWhiteSpace(hypothesis))
            return EntailmentVerdict.Unknown;

        var encoded = _tokenizer.EncodePair(premise, hypothesis, _maxTokens);
        var length = encoded.InputIds.Length;
        if (length == 0)
            return EntailmentVerdict.Unknown;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", Tensor(encoded.InputIds, length)),
            NamedOnnxValue.CreateFromTensor("attention_mask", Tensor(encoded.AttentionMask, length)),
        };

        var logits = await _session.RunAsync(inputs, ct);
        if (logits is null || logits.Length != _labels.Length)
            return EntailmentVerdict.Unknown;

        var probabilities = Softmax(logits);
        var best = 0;
        for (var i = 1; i < probabilities.Length; i++)
            if (probabilities[i] > probabilities[best])
                best = i;

        return new EntailmentVerdict(_labels[best], probabilities[best]);
    }

    /// <summary>
    /// The model's <c>id2label</c>, in index order. Without it there is no honest way to read the
    /// output, so a model whose config is missing or unrecognizable reports itself unavailable
    /// rather than picking an order and hoping.
    /// </summary>
    private static (Entailment[] Labels, string? Reason) LoadLabels(
        CognitiveModelEntry entry, string directory, ILogger logger)
    {
        if (!entry.Enabled)
            return (Array.Empty<Entailment>(), "disabled");

        var path = ConfigPath(entry, directory);
        if (path is null || !File.Exists(path))
            return (Array.Empty<Entailment>(), $"label config not found: {path ?? "(unset)"}");

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("id2label", out var map))
                return (Array.Empty<Entailment>(), "label config has no id2label");

            var byIndex = new SortedDictionary<int, Entailment>();
            foreach (var entry2 in map.EnumerateObject())
            {
                if (!int.TryParse(entry2.Name, out var index))
                    continue;
                var label = entry2.Value.GetString()?.Trim().ToLowerInvariant();
                byIndex[index] = label switch
                {
                    "contradiction" => Entailment.Contradiction,
                    "entailment" => Entailment.Entailment,
                    _ => Entailment.Neutral,
                };
            }

            if (byIndex.Count == 0)
                return (Array.Empty<Entailment>(), "label config had no usable id2label entries");

            var labels = byIndex.Values.ToArray();
            logger.LogInformation("NLI label order: {Labels}", string.Join(", ", labels));
            return (labels, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read NLI label config from {Path}.", path);
            return (Array.Empty<Entailment>(), $"label config failed to load: {ex.Message}");
        }
    }

    private static string? ConfigPath(CognitiveModelEntry entry, string directory)
    {
        if (string.IsNullOrWhiteSpace(entry.Path))
            return null;
        var modelPath = Path.IsPathRooted(entry.Path) ? entry.Path : Path.Combine(directory, entry.Path);
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        return Path.Combine(Path.GetDirectoryName(modelPath) ?? directory, $"{stem}-config.json");
    }

    private static double[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var sum = exp.Sum();
        return sum <= 0 ? exp : exp.Select(e => e / sum).ToArray();
    }

    private static DenseTensor<long> Tensor(long[] values, int length)
        => new(values, new[] { 1, length });

    public void Dispose() => _session.Dispose();
}
