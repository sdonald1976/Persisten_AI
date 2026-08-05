using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Deterministic, offline summarizer. It doesn't write novel prose; it produces a stable
/// higher-level statement that names how many observations it rolls up and preserves their
/// content, so consolidation is verifiable without an LLM. Swap for a real summarizer in prod.
/// </summary>
public sealed class MockSummarizer : ISummarizer
{
    public Task<string> SummarizeAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        var distinct = statements
            .Select(s => s.Trim().TrimEnd('.'))
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        var summary = distinct.Count == 0
            ? "No supporting statements."
            : $"Across {statements.Count} conversations, the user consistently indicated: {string.Join("; ", distinct)}.";

        return Task.FromResult(summary);
    }
}
