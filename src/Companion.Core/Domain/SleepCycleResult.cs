namespace Companion.Core.Domain;

/// <summary>What one idle sleep cycle did (reflection + housekeeping), for logs and demos.</summary>
public sealed record SleepCycleResult
{
    /// <summary>The reflection pass's output, or null when it didn't run (no new material / unusable output).</summary>
    public ReflectionResult? Reflection { get; init; }

    /// <summary>How many consolidated memories the housekeeping created.</summary>
    public int ConsolidatedGroups { get; init; }

    /// <summary>How many stale open curiosities were let go.</summary>
    public int StaleCuriositiesDismissed { get; init; }

    public bool DidAnything => Reflection is not null || ConsolidatedGroups > 0 || StaleCuriositiesDismissed > 0;
}
