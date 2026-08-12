using System.Collections.Concurrent;
using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Memoizes embeddings by exact text. Embedding a string is deterministic, so this is a pure
/// win — and a single turn embeds the SAME user message up to three times (project resolution,
/// open-loop scoring, memory retrieval), each a round trip to the embedding server on the
/// critical response path. The cache collapses those to one call.
///
/// Bounded (oldest-first eviction) so long sessions can't grow it without limit, and every
/// vector is copied in and out: callers store the array they receive (on entities, in the
/// vector index), and shared mutable state across those owners would be a corruption bug
/// waiting to happen. Wraps OUTSIDE the telemetry decorator so a cache hit is not recorded as
/// a model call — the diagnostics then show real provider traffic, which is the point.
/// </summary>
public sealed class CachingEmbeddingModel : IEmbeddingModel
{
    /// <summary>Entries kept; ~3KB each at 768 dimensions, so this is a small fixed budget.</summary>
    private const int Capacity = 256;

    private readonly IEmbeddingModel _inner;
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public CachingEmbeddingModel(IEmbeddingModel inner) => _inner = inner;

    /// <summary>The wrapped model (tests unwrap to assert configuration).</summary>
    public IEmbeddingModel Inner => _inner;

    public int Dimensions => _inner.Dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return await _inner.EmbedAsync(text, ct);

        if (_cache.TryGetValue(text, out var cached))
            return (float[])cached.Clone();

        var vector = await _inner.EmbedAsync(text, ct);
        if (vector.Length == 0)
            return vector; // never cache a degenerate result

        if (_cache.TryAdd(text, (float[])vector.Clone()))
        {
            _order.Enqueue(text);
            while (_cache.Count > Capacity && _order.TryDequeue(out var oldest))
                _cache.TryRemove(oldest, out _);
        }

        return vector;
    }
}
