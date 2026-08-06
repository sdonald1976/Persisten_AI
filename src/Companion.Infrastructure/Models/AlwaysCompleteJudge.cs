using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// A completion judge that always says "complete". Used in the offline/mock configuration (where
/// replies are deterministic and self-contained) and as a safe default when no semantic judge is
/// wired — the generator then falls back to the transport-level <c>finish_reason</c> signal only.
/// </summary>
public sealed class AlwaysCompleteJudge : ICompletionJudge
{
    public Task<bool> IsCompleteAsync(string userRequest, string replySoFar, CancellationToken ct = default)
        => Task.FromResult(true);
}
