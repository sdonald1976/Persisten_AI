namespace Companion.Core.Domain;

/// <summary>What one reflection pass produced (already persisted when returned by the reflector).</summary>
public sealed record ReflectionResult
{
    public required Reflection Reflection { get; init; }

    /// <summary>Curiosities minted by this pass (after validation and dedupe). Empty on a quiet day.</summary>
    public IReadOnlyList<Curiosity> Curiosities { get; init; } = Array.Empty<Curiosity>();
}
