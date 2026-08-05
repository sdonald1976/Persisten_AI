namespace Companion.Core.Domain;

/// <summary>What the post-turn project/open-loop update did (step 10), for diagnostics.</summary>
public sealed record ProjectUpdateResult
{
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();

    public static readonly ProjectUpdateResult Empty = new();
}
