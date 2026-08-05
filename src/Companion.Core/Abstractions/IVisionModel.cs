namespace Companion.Core.Abstractions;

/// <summary>An image handed to a vision model, with its bytes and media type (e.g. "image/png").</summary>
public sealed record ImageInput(byte[] Data, string MediaType);

/// <summary>
/// Vision role: describes / answers questions about one or more images. Used to turn an image
/// the user shares into text the rest of the memory pipeline can store and reason over.
/// </summary>
public interface IVisionModel
{
    Task<string> DescribeAsync(string prompt, IReadOnlyList<ImageInput> images, CancellationToken ct = default);
}
