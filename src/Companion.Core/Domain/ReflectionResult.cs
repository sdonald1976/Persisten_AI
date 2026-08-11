namespace Companion.Core.Domain;

/// <summary>What one reflection pass produced (already persisted when returned by the reflector).</summary>
public sealed record ReflectionResult
{
    public required Reflection Reflection { get; init; }

    /// <summary>Curiosities minted by this pass (after validation and dedupe). Empty on a quiet day.</summary>
    public IReadOnlyList<Curiosity> Curiosities { get; init; } = Array.Empty<Curiosity>();

    /// <summary>Evidence-verified shared moments persisted as shared episodic memories.</summary>
    public IReadOnlyList<EpisodicMemory> SharedMoments { get; init; } = Array.Empty<EpisodicMemory>();

    /// <summary>The companion's own preferences this pass created or evolved.</summary>
    public IReadOnlyList<CompanionPreference> Preferences { get; init; } = Array.Empty<CompanionPreference>();

    public IReadOnlyList<AttentionItem> AttentionItems { get; init; } = Array.Empty<AttentionItem>();
    public IReadOnlyList<MemoryAssociation> Associations { get; init; } = Array.Empty<MemoryAssociation>();
    public IReadOnlyList<Procedure> Procedures { get; init; } = Array.Empty<Procedure>();
    public IReadOnlyList<SharedExperiencePerspective> SharedPerspectives { get; init; } = Array.Empty<SharedExperiencePerspective>();

    /// <summary>How many held curiosities the conversation turned out to have answered.</summary>
    public int SatisfiedCuriosities { get; init; }
}
