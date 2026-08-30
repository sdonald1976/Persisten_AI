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

    /// <summary>
    /// Queues the STRUCTURAL plan evidence for a turn the renderer must not see (Source 2:
    /// tool turns). The V3 row is recorded; no render happens, no comparison is scored, and
    /// no renderer metric moves — the run-1c corpus never covered tool turns, so comparing
    /// on one would be measuring the wrong thing. Same bounded queue, same drop accounting.
    /// </summary>
    void ObservePlanOnly(RendererShadowObservation observation);

    // ---- Run-2: the mouth -------------------------------------------------------------------
    // A sibling arm, not a replacement. Separate because they are different models on different
    // prompt formats at different stages of trust; on the same interface because they share the
    // recorder, the queue and the privacy path, so a row from either is written and forgotten
    // the same way.

    /// <summary>Whether run-2 renders beside the reply at all. Display is a separate question.</summary>
    bool IsMouthObserving { get; }

    /// <summary>
    /// What run-2 has done this process: rendered, failed, displayed, fallen back, and which
    /// adapter the endpoint reported loading. The displayed/fallback split is the number that
    /// says whether a canary is working, so it is a first-class reading rather than something
    /// reconstructed from a truncated trace log.
    /// </summary>
    MouthCounters MouthCounters { get; }

    /// <summary>Whether run-2 may be DISPLAYED to this user. One named user, or nobody.</summary>
    bool IsMouthCanaryFor(string userId);

    /// <summary>
    /// Render this turn through run-2 for DISPLAY, or return null meaning "show production".
    ///
    /// Null covers every failure class without distinguishing them at the call site: the caller
    /// holds the production reply already and does not need to know how the mouth failed in order
    /// to decide what to show. The reason is recorded, never acted on.
    /// </summary>
    Task<RendererCanaryResult?> RenderMouthForDisplayAsync(
        RendererShadowObservation observation, bool record, CancellationToken ct);

    /// <summary>
    /// Render and record run-2 beside the displayed reply, changing nothing. Fire-and-forget on
    /// the same terms as <see cref="Observe"/>: immutable inputs, no return, never throws.
    /// </summary>
    void ObserveMouth(RendererShadowObservation observation);

    /// <summary>
    /// Confirm the endpoint is serving the pinned adapter. A hash in configuration and a process
    /// answering on a port are two different claims, and only the second one renders turns.
    /// </summary>
    Task<(bool Ok, string Detail)> VerifyMouthIdentityAsync(CancellationToken ct);

    /// <summary>Queue lifecycle counters, for diagnostics and the collection report.</summary>
    RendererShadowCounters Counters { get; }

    /// <summary>
    /// Whether this user's eligible turns should DISPLAY the tuned renderer's reply — the
    /// user-scoped canary (docs/RENDERER_SHADOW.md §8). False for everyone unless the
    /// configuration names exactly this user.
    /// </summary>
    bool IsCanaryFor(string userId);

    /// <summary>
    /// Renders the plan synchronously for display. Returns null when the renderer is
    /// unavailable (down, timed out, errored) — the caller keeps the production reply.
    /// A non-null result with <see cref="RendererCanaryResult.CriticalFailure"/> true means
    /// the render completed but failed a critical fidelity check; the caller must fall back.
    /// When <paramref name="record"/> is true, the comparison row is written either way,
    /// with Applied naming the reply that was actually shown. Never throws.
    /// </summary>
    Task<RendererCanaryResult?> RenderForDisplayAsync(
        RendererShadowObservation observation, bool record, CancellationToken ct);
}

/// <summary>One canary render: the candidate reply, its deterministic violations, and the verdict.</summary>
public sealed record RendererCanaryResult(
    string Reply, IReadOnlyList<string> Violations, long LatencyMs, bool CriticalFailure);

/// <summary>
/// The four fates an observation can meet, plus what is still waiting. Queued counts every
/// accepted enqueue; Completed + Failed + Pending always reconciles against it, and Dropped
/// counts what a full queue refused — a number that must appear in the shadow report rather
/// than vanish. CanaryDisplayed/CanaryFallback count the user-scoped canary's outcomes:
/// every eligible canary turn lands in exactly one of them.
/// </summary>
public sealed record RendererShadowCounters(
    long Queued, long Completed, long Failed, long Dropped, int Pending,
    long CanaryDisplayed = 0, long CanaryFallback = 0,
    RendererV3Counters? V3 = null);

/// <summary>
/// P3 shadow-observation lifecycle (docs/RESPONSE_PLAN_V3_SPEC.md §14): every produced
/// envelope lands in exactly one of valid/invalid; protected/redacted count privacy
/// outcomes; failed/dropped count infrastructure outcomes. translated_v2 rows test
/// translation, serialization, privacy, and infrastructure only.
/// </summary>
public sealed record RendererV3Counters(
    long Produced, long Valid, long Invalid, long V2Compatible,
    long Protected, long Redacted, long Failed, long Dropped,
    long NativeBuilt = 0, long NativeBuildFailed = 0, long NativeLintRejects = 0,
    long NativeParityMatch = 0, long NativeParityDiffers = 0,
    long PlanOnly = 0);

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

    /// <summary>
    /// A3 ownership. These rows quote the user's message and both replies verbatim, which
    /// makes them the most sensitive thing the shadow table holds — and until now they had
    /// no owner and no evidence identity, so they could only be forgotten by substring.
    /// </summary>
    public string? UserId { get; init; }

    public Guid? SourceMessageId { get; init; }

    public Guid? ConversationId { get; init; }

    /// <summary>The reply production actually sent, after all filters and gates.</summary>
    public required string ProductionResponse { get; init; }

    /// <summary>
    /// The companion's rendered context packet - the SYSTEM message MouthPromptV4 requires.
    ///
    /// Run-2 was trained on exactly this seat: the mouth occupies the position the chat model
    /// occupies in TurnExecution, and receives the same packet the chat model receives. Passing
    /// the packet rather than rebuilding one here is what makes "the training row's input equals
    /// the shipping renderer's input" true by construction rather than by review.
    ///
    /// Null for the run-1c path, which serializes plan/2 and never sees a packet.
    /// </summary>
    public ContextPacket? Packet { get; init; }

    /// <summary>
    /// plan/4 evidence: the size of the CompactV4 serialization for this turn, when a frame
    /// was present. A NUMBER, never the text — CompactV4 is recorded as evidence that the
    /// serializer runs on real turns, and reaches no model until Run-2 is promoted.
    /// </summary>
    public int? NativeCompactV4Chars { get; init; }

    /// <summary>plan/4 evidence: the frame's transition, when there was one. A token.</summary>
    public string? NativeFrameTransition { get; init; }

    /// <summary>P5/Source 2: the content-safe assembler report for the contributions folded
    /// into <see cref="NativeV3"/>. Ids, decisions, reason codes and counts — no text.</summary>
    public Companion.PlanV3.AssemblyReport? NativeAssembly { get; init; }

    /// <summary>P4: the native_v3 plan built from upstream state, when the build succeeded.
    /// Shadow evidence only — affects nothing.</summary>
    public Companion.PlanV3.PlanV3? NativeV3 { get; init; }

    /// <summary>Content-safe failure reason when the native build threw (exception type +
    /// message head, no plan text).</summary>
    public string? NativeBuildError { get; init; }

    /// <summary>Content-safe source-side lint rejections ("id source rule").</summary>
    public IReadOnlyList<string> NativeLintRejections { get; init; } = [];
}

/// <summary>Run-2's tally for this process.</summary>
public readonly record struct MouthCounters(
    long Rendered, long Failed, long CanaryDisplayed, long CanaryFallback, string? LoadedAdapterSha256);

/// <summary>No-op used when renderer shadow mode is disabled; the flag off IS the rollback.</summary>
public sealed class NullRendererShadow : IRendererShadow
{
    public bool IsObserving => false;

    public void Observe(RendererShadowObservation observation)
    {
    }

    public void ObservePlanOnly(RendererShadowObservation observation)
    {
    }

    public RendererShadowCounters Counters => new(0, 0, 0, 0, 0);

    public bool IsCanaryFor(string userId) => false;

    public Task<RendererCanaryResult?> RenderForDisplayAsync(
        RendererShadowObservation observation, bool record, CancellationToken ct)
        => Task.FromResult<RendererCanaryResult?>(null);

    // The mouth is off here for the same reason everything else is: this type IS the off switch.
    public bool IsMouthObserving => false;

    public MouthCounters MouthCounters => new(0, 0, 0, 0, null);

    public bool IsMouthCanaryFor(string userId) => false;

    public Task<RendererCanaryResult?> RenderMouthForDisplayAsync(
        RendererShadowObservation observation, bool record, CancellationToken ct)
        => Task.FromResult<RendererCanaryResult?>(null);

    public void ObserveMouth(RendererShadowObservation observation)
    {
    }

    public Task<(bool Ok, string Detail)> VerifyMouthIdentityAsync(CancellationToken ct)
        => Task.FromResult((true, "renderer shadow disabled"));
}
