using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// The companion's sleep: while the user is away, think first, then tidy. One cycle runs the
/// reflection pass and — when it actually processed new material — memory consolidation, which
/// until now only ever ran when explicitly commanded. Curiosity hygiene (dropping wonderings that
/// never found their moment) runs every cycle; it's cheap and time-based, not material-based.
///
/// Consolidation is deliberately gated on the reflection having run: no new material means the
/// memory landscape hasn't changed since the last sleep, so there is nothing new to roll up —
/// and a failed model round shouldn't look like a finished night's sleep.
/// </summary>
public sealed class SleepCycle : ISleepCycle
{
    /// <summary>An open wondering older than this has missed its moment — let it go.</summary>
    internal static readonly TimeSpan StaleCuriosityAge = TimeSpan.FromDays(14);

    private readonly IReflector _reflector;
    private readonly IMemoryConsolidator _consolidator;
    private readonly IReflectionStore _reflections;
    private readonly TimeProvider _clock;
    private readonly ILogger<SleepCycle> _logger;

    public SleepCycle(
        IReflector reflector,
        IMemoryConsolidator consolidator,
        IReflectionStore reflections,
        TimeProvider clock,
        ILogger<SleepCycle> logger)
    {
        _reflector = reflector;
        _consolidator = consolidator;
        _reflections = reflections;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SleepCycleResult> RunAsync(string userId, CancellationToken ct = default)
    {
        // 1. Think (the inner monologue). Null = nothing new enough, or the model's output was
        // unusable — either way there is no fresh material behind it.
        var reflection = await _reflector.ReflectAsync(userId, ct);

        // 2. Tidy memory, but only after a real pass over new material.
        var consolidated = 0;
        if (reflection is not null)
            consolidated = (await _consolidator.ConsolidateAsync(userId, ct)).Created.Count;

        // 3. Let stale wonderings go, regardless — staleness is about time, not new material.
        var dismissed = await _reflections.DismissStaleAsync(
            userId, _clock.GetUtcNow() - StaleCuriosityAge, ct);

        var result = new SleepCycleResult
        {
            Reflection = reflection,
            ConsolidatedGroups = consolidated,
            StaleCuriositiesDismissed = dismissed,
        };

        if (result.DidAnything)
        {
            _logger.LogInformation(
                "Sleep cycle for {UserId}: {Kind}, {Curiosities} curiosities minted, " +
                "{Consolidated} groups consolidated, {Dismissed} stale curiosities dismissed.",
                userId,
                reflection is null ? "no reflection" : reflection.Reflection.HasMusing ? "musing written" : "quiet day",
                reflection?.Curiosities.Count ?? 0, consolidated, dismissed);
        }

        return result;
    }
}
