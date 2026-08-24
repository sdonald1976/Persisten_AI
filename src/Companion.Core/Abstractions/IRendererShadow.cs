using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// True shadow mode for the tuned renderer (docs/RENDERER_SHADOW.md): renders the turn's
/// ResponsePlan through the run-1c adapter beside the production reply and records the pair
/// with deterministic check results — the same migration discipline as
/// <see cref="IShadowRecorder"/>, applied to the language organ.
///
/// The contract that makes this a shadow rather than an experiment on live users:
/// <see cref="Observe"/> is fire-and-forget, receives only immutable copies, returns nothing,
/// and its callee touches no conversation state, no memory pipeline, no goals, no tools, and
/// no user-visible output. A failure anywhere inside is a log line.
/// </summary>
public interface IRendererShadow
{
    /// <summary>Whether observation is on at all — callers skip snapshot work when it is not.</summary>
    bool IsObserving { get; }

    /// <summary>
    /// Queues a shadow render of this turn onto a bounded single-consumer channel. Returns
    /// immediately (a full queue drops the observation and counts the drop — it never blocks
    /// a reply); the render, the deterministic checks on both replies, and the recording all
    /// happen on the consumer with a per-item timeout. Never throws.
    /// </summary>
    void Observe(RendererShadowObservation observation);

    /// <summary>Queue lifecycle counters, for diagnostics and the collection report.</summary>
    RendererShadowCounters Counters { get; }
}

/// <summary>
/// The four fates an observation can meet, plus what is still waiting. Queued counts every
/// accepted enqueue; Completed + Failed + Pending always reconciles against it, and Dropped
/// counts what a full queue refused — a number that must appear in the shadow report rather
/// than vanish.
/// </summary>
public sealed record RendererShadowCounters(
    long Queued, long Completed, long Failed, long Dropped, int Pending);

/// <summary>
/// An immutable snapshot of exactly what the shadow renderer is allowed to see: the plan the
/// production reply was measured against, the recent transcript, the user's message, and the
/// production reply itself. Deliberately no ids of live entities beyond the trace id — the
/// shadow path cannot reach back into the turn through this record.
/// </summary>
public sealed record RendererShadowObservation
{
    public required Guid TraceId { get; init; }

    public required ResponsePlan Plan { get; init; }

    /// <summary>Recent prior turns, oldest first, as (role, text) — role is "user" or "assistant".</summary>
    public required IReadOnlyList<(string Role, string Text)> Transcript { get; init; }

    public required string UserMessage { get; init; }

    /// <summary>The reply production actually sent, after all filters and gates.</summary>
    public required string ProductionResponse { get; init; }
}

/// <summary>No-op used when renderer shadow mode is disabled; the flag off IS the rollback.</summary>
public sealed class NullRendererShadow : IRendererShadow
{
    public bool IsObserving => false;

    public void Observe(RendererShadowObservation observation)
    {
    }

    public RendererShadowCounters Counters => new(0, 0, 0, 0, 0);
}
