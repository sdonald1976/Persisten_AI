using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface ICapabilityRegistry
{
    Task<IReadOnlyList<CapabilityDescriptor>> GetAllAsync(CancellationToken ct = default);
    Task<string> RenderSummaryAsync(string? query = null, CancellationToken ct = default);
    Task MarkSuccessAsync(string id, DateTimeOffset now, CancellationToken ct = default);
    Task MarkFailureAsync(string id, DateTimeOffset now, CancellationToken ct = default);
}
