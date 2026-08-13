using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Tests.Fixtures;

internal static class ReflectorTestExtensions
{
    /// <summary>
    /// Runs a reflection pass and returns just the result, or null when it skipped. Most tests are
    /// asserting on what a pass produced, not on why one didn't happen; the tests that care about
    /// the skip reason call <see cref="IReflector.ReflectAsync"/> directly.
    /// </summary>
    public static async Task<ReflectionResult?> ReflectResultAsync(
        this IReflector reflector, string userId, CancellationToken ct = default)
        => (await reflector.ReflectAsync(userId, ct)).Result;
}
