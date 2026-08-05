namespace Companion.Core.Abstractions;

/// <summary>Embedding role. Produces a fixed-length vector for a piece of text.</summary>
public interface IEmbeddingModel
{
    /// <summary>Dimensionality of the vectors this model produces.</summary>
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
