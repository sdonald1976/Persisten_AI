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

    /// <summary>An event this long past that never got its follow-up isn't asked about anymore.</summary>
    public static readonly TimeSpan StaleAnticipationAge = TimeSpan.FromDays(7);

    /// <summary>How long model/tool call telemetry is kept before the sleep pass sweeps it.</summary>
    public static readonly TimeSpan DiagnosticsRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// How long her own experiences are kept. Long enough to reflect on a week she barely spoke
    /// during, short enough that a world running for a year does not accumulate a million rows of
    /// doorways. What survives beyond this is whatever reflection actually made of them.
    /// </summary>
    public static readonly TimeSpan ExperienceRetention = TimeSpan.FromDays(30);

    private readonly IReflector _reflector;
    private readonly IMemoryConsolidator _consolidator;
    private readonly IReflectionStore _reflections;
    private readonly IAnticipationStore _anticipations;
    private readonly IDiagnosticsStore _diagnostics;
    private readonly IExperienceStore _experiences;
    private readonly TimeProvider _clock;
    private readonly ILogger<SleepCycle> _logger;
    private readonly IGapStore? _gaps;

    /// <summary>How long an unpursued knowledge gap is held before aging to Expired.
    /// Diagnostics-grade retention: a gap is working epistemic state, never biography.</summary>
    public static readonly TimeSpan StaleGapAge = TimeSpan.FromDays(30);

    public SleepCycle(
        IReflector reflector,
        IMemoryConsolidator consolidator,
        IReflectionStore reflections,
        IAnticipationStore anticipations,
        IDiagnosticsStore diagnostics,
        IExperienceStore experiences,
        TimeProvider clock,
        ILogger<SleepCycle> logger,
        IGapStore? gaps = null)
    {
        _gaps = gaps;
        _reflector = reflector;
        _consolidator = consolidator;
        _reflections = reflections;
        _anticipations = anticipations;
        _diagnostics = diagnostics;
        _experiences = experiences;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SleepCycleResult> RunAsync(string userId, CancellationToken ct = default)
    {
        // 1. Think (the inner monologue). It may skip — too little new material, or the model's
        // output was unusable — and either way there is no fresh material behind it.
        var reflection = await _reflector.ReflectAsync(userId, ct);

        // 2. Tidy memory, but only after a real pass over new material.
        var consolidated = 0;
        if (reflection.Reflected)
            consolidated = (await _consolidator.ConsolidateAsync(userId, ct)).Created.Count;

        // 3. Let stale things go, regardless — staleness is about time, not new material:
        // wonderings that never found their moment, and passed events that never got followed up.
        var dismissed = await _reflections.DismissStaleAsync(
            userId, _clock.GetUtcNow() - StaleCuriosityAge, ct);
        var expired = await _anticipations.ExpireStaleAsync(
            userId, _clock.GetLocalNow().Date - StaleAnticipationAge, ct);

        // Sweep old telemetry while tidying — a month of model/tool history is plenty for
        // debugging and model comparisons, and the tables stay bounded without a separate job.
        await _diagnostics.PruneAsync(_clock.GetUtcNow() - DiagnosticsRetention, ct);
        await _experiences.PruneAsync(_clock.GetUtcNow() - ExperienceRetention, ct);
        if (_gaps is not null)
            await _gaps.ExpireStaleAsync(userId, _clock.GetUtcNow() - StaleGapAge, ct);

        var result = new SleepCycleResult
        {
            Reflection = reflection.Result,
            ConsolidatedGroups = consolidated,
            StaleCuriositiesDismissed = dismissed,
            StaleAnticipationsExpired = expired,
        };

        if (result.DidAnything)
        {
            _logger.LogInformation(
                "Sleep cycle for {UserId}: {Kind}, {Curiosities} curiosities minted, " +
                "{Consolidated} groups consolidated, {Dismissed} stale curiosities dismissed, " +
                "{Expired} stale anticipations expired.",
                userId,
                reflection.Result is not { } r ? $"no reflection ({reflection.SkipReason})"
                    : r.Reflection.HasMusing ? "musing written" : "quiet day",
                reflection.Result?.Curiosities.Count ?? 0, consolidated, dismissed, expired);
        }

        return result;
    }
}
