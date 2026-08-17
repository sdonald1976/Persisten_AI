using System.Text;
using System.Text.Json;
using Companion.Core;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Byte-level BPE, as RoBERTa-family models expect: <c>&lt;s&gt; A &lt;/s&gt;&lt;/s&gt; B &lt;/s&gt;</c>,
/// no segment ids.
///
/// It is written out by hand rather than configured, because the tokenizer library ships no
/// byte-level pre-tokenizer and no loader for a Hugging Face <c>tokenizer.json</c>. That is a
/// genuinely uncomfortable thing to hand-roll on this path: a tokenizer that is subtly wrong does
/// not throw, it produces plausible ids, and the model then returns confident verdicts about which
/// of the user's memories contradict each other. Wrong in a way that looks exactly like working.
///
/// So it is verified rather than trusted. <c>RobertaTokenizationTests</c> compares its output token
/// for token against ids produced by Hugging Face's own tokenizer for the same model, on pairs
/// including punctuation, contractions and symbols. If that test fails, the ids are wrong and the
/// model must not be used — which is the whole reason the fixture exists.
///
/// The algorithm: map each UTF-8 byte to a printable character (GPT-2's byte encoder, which is why
/// spaces appear as Ġ), split on the GPT-2 pattern, then merge greedily by rank.
/// </summary>
internal sealed class RobertaLikeTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _merges;
    private readonly Dictionary<string, int> _cache = new(StringComparer.Ordinal);
    private readonly int _bos;
    private readonly int _eos;
    private readonly int _unk;

    private RobertaLikeTokenizer(
        Dictionary<string, int>? vocab, Dictionary<(string, string), int>? merges, string? reason)
    {
        _vocab = vocab ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _merges = merges ?? new Dictionary<(string, string), int>();
        Reason = reason;

        _bos = Id("<s>", 0);
        _eos = Id("</s>", 2);
        _unk = Id("<unk>", 3);
    }

    public bool IsAvailable => _vocab.Count > 0;
    public string? Reason { get; }

    public static RobertaLikeTokenizer Load(CognitiveModelEntry entry, string directory, ILogger logger)
    {
        if (!entry.Enabled)
            return new RobertaLikeTokenizer(null, null, "disabled");

        // One file, not two: tokenizer.json is what the model repo actually ships, and deriving
        // vocab/merges from it at load time avoids a conversion step that could drift from it.
        var path = Resolve(entry, directory);
        if (path is null || !File.Exists(path))
            return new RobertaLikeTokenizer(null, null, $"tokenizer not found: {path ?? "(unset)"}");

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var model = doc.RootElement.GetProperty("model");

            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in model.GetProperty("vocab").EnumerateObject())
                vocab[token.Name] = token.Value.GetInt32();

            var merges = new Dictionary<(string, string), int>();
            var rank = 0;
            foreach (var merge in model.GetProperty("merges").EnumerateArray())
            {
                // Newer exports use ["a","b"]; older ones a single "a b" string.
                string left, right;
                if (merge.ValueKind == JsonValueKind.Array)
                {
                    left = merge[0].GetString()!;
                    right = merge[1].GetString()!;
                }
                else
                {
                    var parts = merge.GetString()!.Split(' ', 2);
                    if (parts.Length != 2)
                        continue;
                    (left, right) = (parts[0], parts[1]);
                }
                merges.TryAdd((left, right), rank++);
            }

            return new RobertaLikeTokenizer(vocab, merges, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load a byte-level BPE tokenizer from {Path}.", path);
            return new RobertaLikeTokenizer(null, null, $"tokenizer failed to load: {ex.Message}");
        }
    }

    /// <summary>Encodes a premise/hypothesis pair the way this model family was trained to see one.</summary>
    public EncodedPair EncodePair(string a, string b, int maxTokens)
    {
        if (!IsAvailable)
            return new EncodedPair(Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>());

        // Four structural tokens: <s> A </s> </s> B </s>.
        var room = Math.Max(2, maxTokens - 4);
        var first = Encode(a, room / 2);
        var second = Encode(b, room - first.Count);

        var ids = new List<long>(first.Count + second.Count + 4) { _bos };
        ids.AddRange(first);
        ids.Add(_eos);
        ids.Add(_eos);
        ids.AddRange(second);
        ids.Add(_eos);

        var mask = Enumerable.Repeat(1L, ids.Count).ToArray();
        // RoBERTa has no segment embeddings; the field is returned empty and the caller omits it.
        return new EncodedPair(ids.ToArray(), mask, Array.Empty<long>());
    }

    private List<long> Encode(string text, int limit)
    {
        var ids = new List<long>();
        if (limit <= 0 || string.IsNullOrEmpty(text))
            return ids;

        foreach (var word in ByteLevel.Split(text))
        {
            foreach (var piece in BpeMerge(word))
            {
                ids.Add(_vocab.TryGetValue(piece, out var id) ? id : _unk);
                if (ids.Count >= limit)
                    return ids;
            }
        }
        return ids;
    }

    /// <summary>Greedily applies the highest-ranked merge until none applies — standard BPE.</summary>
    private List<string> BpeMerge(string word)
    {
        if (_cache.TryGetValue(word, out var single))
            return new List<string> { _vocab.First(v => v.Value == single).Key };

        var symbols = word.Select(c => c.ToString()).ToList();
        if (symbols.Count <= 1)
            return symbols;

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_merges.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
                break;

            symbols[bestIndex] += symbols[bestIndex + 1];
            symbols.RemoveAt(bestIndex + 1);
        }
        return symbols;
    }

    private int Id(string token, int fallback)
        => _vocab.TryGetValue(token, out var id) ? id : fallback;

    private static string? Resolve(CognitiveModelEntry entry, string directory)
    {
        if (!string.IsNullOrWhiteSpace(entry.VocabPath))
        {
            return Path.IsPathRooted(entry.VocabPath)
                ? entry.VocabPath
                : Path.Combine(directory, entry.VocabPath);
        }
        if (string.IsNullOrWhiteSpace(entry.Path))
            return null;
        var modelPath = Path.IsPathRooted(entry.Path) ? entry.Path : Path.Combine(directory, entry.Path);
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        return Path.Combine(Path.GetDirectoryName(modelPath) ?? directory, $"{stem}-tokenizer.json");
    }
}

/// <summary>
/// GPT-2's byte-to-character mapping and word split, which every RoBERTa-family tokenizer is built
/// on. Bytes are mapped into printable characters so that BPE only ever sees text — which is why a
/// leading space shows up as "Ġ" and why the merge table is expressed in those characters.
/// </summary>
internal static class ByteLevel
{
    private static readonly char[] Encoder = BuildEncoder();

    /// <summary>
    /// Splits the way GPT-2's regex does: contractions, then letter runs, digit runs, symbol runs,
    /// each keeping the space that precedes it. Written out rather than regex'd so the behaviour is
    /// visible — the fixture test is what proves it matches.
    /// </summary>
    public static List<string> Split(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var leadingSpace = text[i] == ' ';
            if (leadingSpace)
                i++;

            if (i < text.Length && text[i] == '\'' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                // 's, 't, 're, 've, 'm, 'll, 'd — kept with the apostrophe, never split off it.
                i++;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
            }
            else if (i < text.Length && char.IsLetter(text[i]))
            {
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
            }
            else if (i < text.Length && char.IsDigit(text[i]))
            {
                while (i < text.Length && char.IsDigit(text[i]))
                    i++;
            }
            else if (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !char.IsLetterOrDigit(text[i]))
                    i++;
            }
            else
            {
                // A run of whitespace: all but the last belongs to this piece, the last to the next.
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i < text.Length)
                    i--;
            }

            if (i == start)
                i++;
            words.Add(Encode(text[start..i]));
        }
        return words;
    }

    /// <summary>UTF-8 bytes, each mapped to its printable stand-in.</summary>
    public static string Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append(Encoder[b]);
        return sb.ToString();
    }

    private static char[] BuildEncoder()
    {
        var map = new char[256];
        var used = new bool[256];

        void Keep(int from, int to)
        {
            for (var b = from; b <= to; b++)
            {
                map[b] = (char)b;
                used[b] = true;
            }
        }

        // The printable ASCII and Latin-1 ranges keep their own character…
        Keep('!', '~');
        Keep(0xA1, 0xAC);
        Keep(0xAE, 0xFF);

        // …and everything else (space, control bytes, 0xAD) is shifted into a private range so it
        // is visible and unambiguous. This is the mapping that turns a leading space into "Ġ".
        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            if (used[b])
                continue;
            map[b] = (char)(256 + next);
            next++;
        }
        return map;
    }
}
