using Companion.Core.Abstractions;
using Companion.Core.Text;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Deterministic, offline embedding: a hashed bag-of-stemmed-words, L2-normalized. Texts
/// that share content words get a high cosine, which makes retrieval behave sensibly in
/// tests and demos with no network or GPU. Uses the same <see cref="Tokenizer"/> as keyword
/// matching so the two signals stay consistent. Swap for a real embedding model in prod.
/// </summary>
public sealed class MockEmbeddingModel : IEmbeddingModel
{
    public int Dimensions { get; }

    public MockEmbeddingModel(int dimensions = 128) => Dimensions = dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vec = new float[Dimensions];
        foreach (var token in Tokenizer.Tokenize(text))
        {
            var idx = (int)(Hash(token) % (uint)Dimensions);
            vec[idx] += 1f;
        }

        // L2 normalize so cosine is scale-invariant.
        double norm = 0;
        foreach (var v in vec) norm += v * v;
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < vec.Length; i++)
                vec[i] = (float)(vec[i] / norm);

        return Task.FromResult(vec);
    }

    // FNV-1a — stable across runs and processes (unlike string.GetHashCode()).
    private static uint Hash(string s)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
