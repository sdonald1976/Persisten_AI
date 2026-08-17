using Companion.Core;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Turns a text pair into the ids a BERT-family encoder expects.
///
/// The tokenizer has to be the one the model was trained with — a WordPiece vocabulary is not
/// interchangeable, and a mismatched one produces ids that are silently, fluently wrong rather than
/// an error. So it is loaded from the <c>vocab.txt</c> shipped beside the model, and a model whose
/// vocabulary is missing reports itself unavailable rather than guessing.
/// </summary>
internal sealed class BertLikeTokenizer
{
    private readonly BertTokenizer? _tokenizer;

    private BertLikeTokenizer(BertTokenizer? tokenizer, string? reason)
    {
        _tokenizer = tokenizer;
        Reason = reason;
    }

    public bool IsAvailable => _tokenizer is not null;
    public string? Reason { get; }

    public static BertLikeTokenizer Load(CognitiveModelEntry entry, string directory, ILogger logger)
    {
        if (!entry.Enabled)
            return new BertLikeTokenizer(null, "disabled");

        var path = ResolveVocab(entry, directory);
        if (path is null || !File.Exists(path))
            return new BertLikeTokenizer(null, $"tokenizer vocabulary not found: {path ?? "(unset)"}");

        try
        {
            using var stream = File.OpenRead(path);
            return new BertLikeTokenizer(BertTokenizer.Create(stream), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load tokenizer vocabulary from {Path}.", path);
            return new BertLikeTokenizer(null, $"tokenizer failed to load: {ex.Message}");
        }
    }

    /// <summary>
    /// Encodes <c>[CLS] a [SEP] b [SEP]</c> with the segment ids that tell the model which half is
    /// which, truncated to <paramref name="maxTokens"/>.
    /// </summary>
    public EncodedPair EncodePair(string a, string b, int maxTokens)
    {
        if (_tokenizer is null)
            return new EncodedPair(Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>());

        var (cls, sep) = SpecialTokens();

        // Reserve the three structural tokens before splitting what is left between the two texts.
        var room = Math.Max(2, maxTokens - 3);
        var first = Encode(a, room / 2);
        var second = Encode(b, room - first.Count);

        var ids = new List<long>(first.Count + second.Count + 3) { cls };
        var types = new List<long>(ids.Capacity) { 0 };

        foreach (var id in first) { ids.Add(id); types.Add(0); }
        ids.Add(sep); types.Add(0);
        foreach (var id in second) { ids.Add(id); types.Add(1); }
        ids.Add(sep); types.Add(1);

        // No padding: a batch of one needs none, and every position is real.
        var mask = Enumerable.Repeat(1L, ids.Count).ToArray();
        return new EncodedPair(ids.ToArray(), mask, types.ToArray());
    }

    /// <summary>
    /// The ids of <c>[CLS]</c> and <c>[SEP]</c>, read out of the loaded vocabulary by encoding the
    /// tokens themselves. Looking them up beats both hardcoding (101/102 is only true for the
    /// bert-base-uncased family) and reading a property name that moves between tokenizer library
    /// versions — the vocabulary is the thing that actually defines them.
    /// </summary>
    private (long Cls, long Sep) SpecialTokens()
    {
        var cls = LookUp("[CLS]") ?? 101;
        var sep = LookUp("[SEP]") ?? 102;
        return (cls, sep);
    }

    private long? LookUp(string token)
    {
        var ids = _tokenizer!.EncodeToIds(token, addSpecialTokens: false);
        // A vocabulary that has the token encodes it as exactly one id; one that doesn't will split
        // it into WordPiece fragments, and that is the signal to fall back rather than use a fragment.
        return ids.Count == 1 ? ids[0] : null;
    }

    private List<long> Encode(string text, int limit)
    {
        if (limit <= 0 || string.IsNullOrEmpty(text))
            return new List<long>();

        var ids = _tokenizer!
            .EncodeToIds(text, addSpecialTokens: false)
            .Select(id => (long)id)
            .ToList();

        return ids.Count <= limit ? ids : ids.GetRange(0, limit);
    }

    private static string? ResolveVocab(CognitiveModelEntry entry, string directory)
    {
        if (!string.IsNullOrWhiteSpace(entry.VocabPath))
        {
            return Path.IsPathRooted(entry.VocabPath)
                ? entry.VocabPath
                : Path.Combine(directory, entry.VocabPath);
        }

        // Default: vocab.txt beside the model, which is what a Hugging Face export produces.
        if (string.IsNullOrWhiteSpace(entry.Path))
            return null;
        var modelPath = Path.IsPathRooted(entry.Path) ? entry.Path : Path.Combine(directory, entry.Path);
        return Path.Combine(Path.GetDirectoryName(modelPath) ?? directory, "vocab.txt");
    }
}

/// <summary>One encoded text pair, ready for the graph.</summary>
internal readonly record struct EncodedPair(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);
