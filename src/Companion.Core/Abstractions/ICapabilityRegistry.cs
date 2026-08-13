using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface ICapabilityRegistry
{
    Task<IReadOnlyList<CapabilityDescriptor>> GetAllAsync(CancellationToken ct = default);
    Task<string> RenderSummaryAsync(string? query = null, CancellationToken ct = default);
    Task MarkSuccessAsync(string id, DateTimeOffset now, CancellationToken ct = default);
    Task MarkFailureAsync(string id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Records what the provider says it actually serves, keyed by model name: true = present,
    /// false = definitively absent. Models the probe couldn't check must be omitted rather than
    /// passed as false — "unreachable" is not "missing", and treating it as such would condemn a
    /// whole working roster over one flaky request.
    /// </summary>
    Task ApplyModelProbeAsync(
        IReadOnlyDictionary<string, bool> presenceByModel, DateTimeOffset now, CancellationToken ct = default);
}
