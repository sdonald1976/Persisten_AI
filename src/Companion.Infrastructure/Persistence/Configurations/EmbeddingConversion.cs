using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Companion.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores embeddings as a compact BLOB (float32 little-endian) rather than JSON. Embeddings
/// are a derived index, so this conversion is lossless round-tripping of the authoritative
/// float vector to bytes and back.
/// </summary>
internal static class EmbeddingConversion
{
    public static readonly ValueConverter<float[], byte[]> Converter = new(
        v => ToBytes(v),
        v => ToFloats(v));

    public static readonly ValueComparer<float[]> Comparer = new(
        (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
        v => v.Aggregate(17, (h, f) => HashCode.Combine(h, f)),
        v => v.ToArray());

    private static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] v)
    {
        var floats = new float[v.Length / sizeof(float)];
        Buffer.BlockCopy(v, 0, floats, 0, v.Length);
        return floats;
    }
}
