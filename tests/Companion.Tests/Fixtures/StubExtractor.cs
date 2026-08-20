using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Tests.Fixtures;

/// <summary>An extractor that returns a fixed set of candidates, for precise pipeline tests.</summary>
public sealed class StubExtractor : IMemoryExtractor
{
    private readonly IReadOnlyList<MemoryCandidate> _candidates;

    public StubExtractor(params MemoryCandidate[] candidates) => _candidates = candidates;

    public Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
        string userId, IReadOnlyList<Message> exchange,
        ReferenceResolution? resolution = null, CancellationToken ct = default)
        => Task.FromResult(_candidates);
}
