using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.PlanV3;
using Companion.RendererBench;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Infrastructure.Renderer;

/// <summary>
/// Renders eligible real plans through the tuned adapter beside production and records the
/// pair (docs/RENDERER_SHADOW.md). The isolation contract, in order of importance:
///
///  1. <see cref="Observe"/> is a bounded-channel TryWrite over an immutable snapshot: it
///     returns immediately whether the queue accepts or is full (a full queue counts a drop —
///     it never blocks a reply), and the shadow path has no handle back into conversation
///     state, memory, goals, or tools.
///  2. One consumer processes observations strictly one at a time, so serve_tuned — a
///     single-threaded measurement instrument on a small GPU — never sees concurrent
///     requests from us, and a burst of turns becomes queue depth, not connection pileup.
///  3. Every failure is counted and logged, never thrown; the counters are part of the
///     shadow report, because "how often did the instrument fail" is data too.
///  4. On graceful shutdown, disposal stops accepting work and drains what is queued within
///     a bounded window; whatever the window cannot fit is counted as dropped, loudly.
/// </summary>
public sealed class RendererShadowService : IRendererShadow, IAsyncDisposable
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Queue depth. At ~8s per shadow render, 16 items is about two minutes of backlog —
    /// deeper than any real conversation burst, shallow enough that a stuck server turns
    /// into visible drop counts instead of an unbounded memory of stale turns.
    /// </summary>
    private const int QueueCapacity = 16;

    private readonly TimeSpan _drainWindow;

    /// <summary>Queue entries: a full shadow render, or a P3 v3-envelope-only recording
    /// (used for canary turns whose plan2 row was written synchronously).</summary>
    private readonly record struct QueueEntry(RendererShadowObservation Obs, bool V3Only);

    private readonly Channel<QueueEntry> _queue;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IShadowRecorder _recorder;
    private readonly RendererShadowOptions _options;
    private readonly ILogger<RendererShadowService> _logger;

    private long _queued;
    private long _completed;
    private long _failed;
    private long _dropped;

    private readonly HttpClient _http;

    private long _canaryDisplayed;
    private long _canaryFallback;
    private long _mouthRendered;
    private long _mouthFailed;
    private long _mouthCanaryDisplayed;
    private long _mouthCanaryFallback;
    private string? _mouthLoadedAdapterSha;

    private long _v3Produced;
    private long _v3Valid;
    private long _v3Invalid;
    private long _v3Compatible;
    private long _v3Protected;
    private long _v3Redacted;
    private long _v3Failed;
    private long _v3Dropped;
    private long _v3PlanOnly;
    private long _v3NativeBuilt;
    private long _v3NativeBuildFailed;
    private long _v3NativeLintRejects;
    private long _v3NativeParityMatch;
    private long _v3NativeParityDiffers;

    public RendererShadowService(
        IShadowRecorder recorder,
        IOptions<CompanionOptions> options,
        ILogger<RendererShadowService> logger)
        : this(recorder, options, logger, drainWindow: null)
    {
    }

    /// <summary>Test seam: short drain window and injectable HttpClient. Behavior is identical.</summary>
    internal RendererShadowService(
        IShadowRecorder recorder,
        IOptions<CompanionOptions> options,
        ILogger<RendererShadowService> logger,
        TimeSpan? drainWindow,
        HttpClient? http = null)
    {
        _drainWindow = drainWindow ?? TimeSpan.FromSeconds(45);
        _http = http ?? Http;
        _recorder = recorder;
        _options = options.Value.RendererShadow;
        _logger = logger;
        _queue = Channel.CreateBounded<QueueEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite returning false on a full queue is the drop signal Observe counts;
            // Wait would be a hidden way for the shadow to slow the world down.
            FullMode = BoundedChannelFullMode.Wait,
        });
        _consumer = _options.Enabled ? Task.Run(ConsumeAsync) : Task.CompletedTask;
    }

    public bool IsObserving => _options.Enabled && _recorder.IsRecording;

    public RendererShadowCounters Counters => new(
        Interlocked.Read(ref _queued),
        Interlocked.Read(ref _completed),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _dropped),
        _queue.Reader.Count,
        Interlocked.Read(ref _canaryDisplayed),
        Interlocked.Read(ref _canaryFallback),
        new RendererV3Counters(
            Interlocked.Read(ref _v3Produced),
            Interlocked.Read(ref _v3Valid),
            Interlocked.Read(ref _v3Invalid),
            Interlocked.Read(ref _v3Compatible),
            Interlocked.Read(ref _v3Protected),
            Interlocked.Read(ref _v3Redacted),
            Interlocked.Read(ref _v3Failed),
            Interlocked.Read(ref _v3Dropped),
            Interlocked.Read(ref _v3NativeBuilt),
            Interlocked.Read(ref _v3NativeBuildFailed),
            Interlocked.Read(ref _v3NativeLintRejects),
            Interlocked.Read(ref _v3NativeParityMatch),
            Interlocked.Read(ref _v3NativeParityDiffers),
            Interlocked.Read(ref _v3PlanOnly)));

    public bool IsCanaryFor(string userId)
        => _options.Enabled
           && !string.IsNullOrEmpty(_options.CanaryUserId)
           && string.Equals(_options.CanaryUserId, userId, StringComparison.Ordinal);

    /// <summary>
    /// The failure classes that must never reach the user even in a canary: an empty reply,
    /// spoken control machinery, recited plan text, a silently dropped required question, or
    /// third-person narration. Softer proxies (palette, sludge, omission heuristics) are
    /// review material, not fallback triggers — they are too false-positive-prone to let a
    /// heuristic override the reply a human is about to read.
    /// </summary>
    private static bool IsCritical(string violation)
        => violation.StartsWith("empty", StringComparison.Ordinal)
           || violation.StartsWith("artifact:", StringComparison.Ordinal)
           || violation.StartsWith("plan-echo", StringComparison.Ordinal)
           || violation.StartsWith("mandatory-question-missing", StringComparison.Ordinal);

    public async Task<RendererCanaryResult?> RenderForDisplayAsync(
        RendererShadowObservation obs, bool record, CancellationToken ct)
    {
        if (!_options.Enabled)
            return null;

        RenderCore core;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.CanaryTimeoutSeconds, 5, 120)));
            core = await RenderCoreAsync(obs, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unavailable, timed out, or broken: the production reply is already in hand and
            // the fallback costs nothing further. Counted, logged, never thrown.
            Interlocked.Increment(ref _canaryFallback);
            _logger.LogWarning(ex, "Renderer canary unavailable for {TraceId}; production reply shown.", obs.TraceId);
            return null;
        }

        var shadowViolations = RendererShadowChecks.Score(obs.Plan, core.Reply);
        var critical = shadowViolations.Any(IsCritical);
        Interlocked.Increment(ref critical ? ref _canaryFallback : ref _canaryDisplayed);

        if (record && _recorder.IsRecording)
        {
            var productionViolations = RendererShadowChecks.Score(obs.Plan, obs.ProductionResponse);
            await RecordComparisonAsync(obs, core, shadowViolations, productionViolations,
                applied: critical ? "legacy" : "model", CancellationToken.None);

            // P3: v3 envelope for canary turns rides the bounded queue — TryWrite only,
            // so the displayed reply's latency is untouched; a full queue counts a drop.
            if (!_queue.Writer.TryWrite(new QueueEntry(obs, V3Only: true)))
                Interlocked.Increment(ref _v3Dropped);
        }

        return new RendererCanaryResult(core.Reply, shadowViolations, core.LatencyMs, critical);
    }

    /// <summary>
    /// Source 2: structural evidence only. The renderer is never invoked, so no comparison
    /// row and no renderer counter moves — only the V3 row is written.
    /// </summary>
    public void ObservePlanOnly(RendererShadowObservation observation)
    {
        if (!IsObserving)
            return;

        if (_queue.Writer.TryWrite(new QueueEntry(observation, V3Only: true)))
            Interlocked.Increment(ref _v3PlanOnly);
        else
            Interlocked.Increment(ref _v3Dropped);
    }

    public void Observe(RendererShadowObservation observation)
    {
        if (!IsObserving)
            return;

        if (_queue.Writer.TryWrite(new QueueEntry(observation, V3Only: false)))
        {
            Interlocked.Increment(ref _queued);
        }
        else
        {
            var dropped = Interlocked.Increment(ref _dropped);
            _logger.LogWarning(
                "Renderer shadow queue full; observation for {TraceId} dropped ({Dropped} total).",
                observation.TraceId, dropped);
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            var observation = entry.Obs;
            if (!entry.V3Only)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)));
                    await RenderAndRecordAsync(observation, cts.Token);
                    Interlocked.Increment(ref _completed);
                }
                catch (Exception ex)
                {
                    // A dead endpoint, a timeout, a serialization bug: a counted data point and a
                    // log line. The whole point of a shadow is that being wrong here is cheap.
                    Interlocked.Increment(ref _failed);
                    _logger.LogDebug(ex, "Renderer shadow observation failed for {TraceId}.", observation.TraceId);
                }
            }

            // P3: the V3 envelope row, recorded beside the plan2 row. Never model-facing,
            // never latency-facing (this consumer is off the turn path), privacy-applied
            // before anything persists.
            try
            {
                await RecordV3RowAsync(observation, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _v3Failed);
                _logger.LogDebug(ex, "V3 shadow row failed for {TraceId}.", observation.TraceId);
            }
        }
    }

    /// <summary>
    /// Graceful shutdown: stop accepting, then let the consumer finish what is already
    /// queued inside the drain window. What the window cannot fit is counted as dropped —
    /// a silent loss on shutdown would understate every rate the report cares about.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        var finished = await Task.WhenAny(_consumer, Task.Delay(_drainWindow)) == _consumer;
        if (!finished)
        {
            var abandoned = _queue.Reader.Count;
            if (abandoned > 0)
                Interlocked.Add(ref _dropped, abandoned);
            _stopping.Cancel();
            _logger.LogWarning(
                "Renderer shadow drain window elapsed with {Abandoned} queued observations abandoned.",
                abandoned);
            try
            {
                await _consumer;
            }
            catch (Exception)
            {
                // Already counted; shutdown owes nobody an exception.
            }
        }
        _stopping.Dispose();
    }

    private async Task RenderAndRecordAsync(RendererShadowObservation obs, CancellationToken ct)
    {
        var core = await RenderCoreAsync(obs, ct);
        var shadowViolations = RendererShadowChecks.Score(obs.Plan, core.Reply);
        var productionViolations = RendererShadowChecks.Score(obs.Plan, obs.ProductionResponse);
        await RecordComparisonAsync(obs, core, shadowViolations, productionViolations, "legacy", ct);
    }

    // ---- Run-2: the mouth -------------------------------------------------------------------

    /// <summary>What the endpoint says it actually loaded, once asked. Null until then.</summary>
    public string? MouthLoadedAdapterSha => _mouthLoadedAdapterSha;

    public long MouthRendered => Interlocked.Read(ref _mouthRendered);
    public long MouthFailed => Interlocked.Read(ref _mouthFailed);
    public long MouthCanaryDisplayed => Interlocked.Read(ref _mouthCanaryDisplayed);
    public long MouthCanaryFallback => Interlocked.Read(ref _mouthCanaryFallback);

    /// <summary>
    /// Whether run-2 may be DISPLAYED to this user. Distinct from whether it is observed: shadow
    /// runs for everyone the options enable, display runs for exactly one named user.
    /// </summary>
    public bool IsMouthCanaryFor(string userId)
        => _options.Mouth.Enabled
           && !string.IsNullOrEmpty(_options.Mouth.CanaryUserId)
           && string.Equals(_options.Mouth.CanaryUserId, userId, StringComparison.Ordinal);

    public bool IsMouthObserving => _options.Mouth.Enabled;

    /// <summary>
    /// Confirm the endpoint is serving the adapter configuration pins. Called once at startup:
    /// a hash in a config file and a process answering on a port are two separate claims, and
    /// only the second one renders turns.
    /// </summary>
    public async Task<(bool Ok, string Detail)> VerifyMouthIdentityAsync(CancellationToken ct)
    {
        if (!_options.Mouth.Enabled)
            return (true, "mouth disabled");
        try
        {
            using var response = await _http.GetAsync(
                $"{_options.Mouth.Endpoint.TrimEnd('/')}/api/identity", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var loaded = doc.RootElement.GetProperty("adapterSha256").GetString() ?? "";
            _mouthLoadedAdapterSha = loaded;

            var pinned = _options.Mouth.AdapterSha256;
            if (string.IsNullOrWhiteSpace(pinned))
                return (false, $"no AdapterSha256 pinned; endpoint is serving {loaded}");
            if (!string.Equals(pinned, loaded, StringComparison.OrdinalIgnoreCase))
                return (false, $"adapter mismatch: pinned {pinned}, endpoint loaded {loaded}");
            return (true, $"endpoint serving pinned adapter {loaded}");
        }
        catch (Exception ex)
        {
            return (false, $"mouth endpoint unreachable: {ex.Message}");
        }
    }

    /// <summary>
    /// Render this turn through run-2 and score it. Used by both the shadow path and the canary;
    /// the difference between them is what the caller does with the result, never how it is
    /// produced, so a canary reply is the same reply the shadow would have recorded.
    /// </summary>
    private async Task<RenderCore> RenderMouthCoreAsync(
        RendererShadowObservation obs, CancellationToken ct)
    {
        if (obs.Packet is null)
            throw new InvalidOperationException("mouth render requires the turn's ContextPacket");
        if (obs.NativeV3 is null)
            throw new InvalidOperationException("mouth render requires the native plan/4");

        // THE training input, built by the one definition of it. Not a reconstruction.
        var prompt = MouthPromptV4.Build(
            obs.Packet, obs.NativeV3, obs.Transcript, obs.UserMessage);
        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt.User)))
            .ToLowerInvariant();

        var payload = new
        {
            model = "run-2",
            stream = false,
            // Greedy, matching the evaluation harness. Sampling would make each turn a
            // measurement of luck rather than of the model.
            options = new { temperature = 0.0, num_predict = 220 },
            messages = new object[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User },
            },
        };

        var started = Stopwatch.GetTimestamp();
        using var response = await _http.PostAsync(
            $"{_options.Mouth.Endpoint.TrimEnd('/')}/api/chat",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var reply = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        var latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The endpoint stamps every reply with the weights that produced it, so a row records
        // what answered rather than what was configured.
        if (doc.RootElement.TryGetProperty("adapter_sha256", out var sha))
            _mouthLoadedAdapterSha = sha.GetString();

        long vramBytes = 0;
        try
        {
            using var ps = await _http.GetAsync($"{_options.Mouth.Endpoint.TrimEnd('/')}/api/ps", ct);
            using var psDoc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync(ct));
            vramBytes = psDoc.RootElement.GetProperty("models")[0].GetProperty("size_vram").GetInt64();
        }
        catch (Exception)
        {
            // Telemetry; its absence never costs the row.
        }

        return new RenderCore(reply, prompt.User, planHash, latencyMs, vramBytes);
    }

    /// <summary>
    /// The canary: run-2's reply, or null to mean "show production".
    ///
    /// Null is returned for every failure class without distinction at the call site - the caller
    /// has the production reply already and does not need to know which way the mouth failed to
    /// decide what to show. The reason is recorded, not acted on.
    /// </summary>
    public async Task<RendererCanaryResult?> RenderMouthForDisplayAsync(
        RendererShadowObservation obs, bool record, CancellationToken ct)
    {
        if (!_options.Mouth.Enabled)
            return null;

        RenderCore core;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(_options.Mouth.CanaryTimeoutSeconds, 5, 180)));
            core = await RenderMouthCoreAsync(obs, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _mouthFailed);
            Interlocked.Increment(ref _mouthCanaryFallback);
            _logger.LogWarning(ex, "Mouth canary unavailable for {TraceId}; production reply shown.", obs.TraceId);
            return null;
        }

        Interlocked.Increment(ref _mouthRendered);
        var violations = RendererShadowChecks.Score(obs.Plan, core.Reply);
        var critical = violations.Any(IsCritical);
        Interlocked.Increment(ref critical ? ref _mouthCanaryFallback : ref _mouthCanaryDisplayed);

        if (record && _recorder.IsRecording)
        {
            var productionViolations = RendererShadowChecks.Score(obs.Plan, obs.ProductionResponse);
            await RecordComparisonAsync(obs, core, violations, productionViolations,
                applied: critical ? "legacy" : "mouth", CancellationToken.None,
                adapterSha256: _mouthLoadedAdapterSha ?? _options.Mouth.AdapterSha256,
                modelVersion: _options.Mouth.ModelVersion);
        }

        return new RendererCanaryResult(core.Reply, violations, core.LatencyMs, critical);
    }

    /// <summary>
    /// Shadow: render run-2, score it, record it, display nothing. Fire and forget - the caller
    /// is not waiting and a failure anywhere inside is a counter and a log line.
    /// </summary>
    public void ObserveMouth(RendererShadowObservation observation)
    {
        if (!_options.Mouth.Enabled)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Clamp(_options.Mouth.TimeoutSeconds, 5, 600)));
                var core = await RenderMouthCoreAsync(observation, cts.Token);
                Interlocked.Increment(ref _mouthRendered);

                if (_recorder.IsRecording)
                {
                    var violations = RendererShadowChecks.Score(observation.Plan, core.Reply);
                    var productionViolations =
                        RendererShadowChecks.Score(observation.Plan, observation.ProductionResponse);
                    await RecordComparisonAsync(observation, core, violations, productionViolations,
                        applied: "legacy", CancellationToken.None,
                        adapterSha256: _mouthLoadedAdapterSha ?? _options.Mouth.AdapterSha256,
                        modelVersion: _options.Mouth.ModelVersion);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _mouthFailed);
                _logger.LogWarning(ex, "Mouth shadow render failed for {TraceId}.", observation.TraceId);
            }
        });
    }

    private readonly record struct RenderCore(string Reply, string Plan2, string PlanHash, long LatencyMs, long VramBytes);

    private async Task<RenderCore> RenderCoreAsync(RendererShadowObservation obs, CancellationToken ct)
    {
        var plan2 = SerializeViaV3Hop(obs.Plan);
        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan2)))
            .ToLowerInvariant();
        var userPrompt = PlanSerialization.BuildUserPrompt(
            "v2", obs.Plan, obs.Transcript, obs.UserMessage);

        object options = _options.NumGpu is { } numGpu
            ? new { temperature = 0.6, num_predict = 220, num_gpu = numGpu }
            : new { temperature = 0.6, num_predict = 220 };
        var payload = new
        {
            // Same name as always; read from options so bootstrap and this call cannot drift.
            model = _options.OllamaModel,
            stream = false,
            options,
            messages = new object[]
            {
                new { role = "system", content = PlanSerialization.SystemPromptV2 },
                new { role = "user", content = userPrompt },
            },
        };

        var started = Stopwatch.GetTimestamp();
        using var response = await _http.PostAsync(
            $"{_options.Endpoint.TrimEnd('/')}/api/chat",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var reply = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        var latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        long vramBytes = 0;
        try
        {
            using var ps = await _http.GetAsync($"{_options.Endpoint.TrimEnd('/')}/api/ps", ct);
            using var psDoc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync(ct));
            vramBytes = psDoc.RootElement.GetProperty("models")[0].GetProperty("size_vram").GetInt64();
        }
        catch (Exception)
        {
            // VRAM is nice-to-have telemetry; its absence never costs the row.
        }

        return new RenderCore(reply, plan2, planHash, latencyMs, vramBytes);
    }

    private async Task RecordComparisonAsync(
        RendererShadowObservation obs, RenderCore core,
        List<string> shadowViolations, List<string> productionViolations,
        string applied, CancellationToken ct,
        // WHICH model produced core.Reply. Defaulted to run-1c because that is what every existing
        // caller means, and passed explicitly by the mouth: a row that records run-1c's adapter
        // hash beside run-2's output is not evidence, it is a mislabelled sample.
        string? adapterSha256 = null, string? modelVersion = null)
    {
        var envelope = JsonSerializer.Serialize(new RendererShadowEnvelope
        {
            PlanHash = core.PlanHash,
            AdapterSha256 = adapterSha256 ?? _options.AdapterSha256,
            ModelVersion = modelVersion ?? _options.ModelVersion,
            LatencyMs = core.LatencyMs,
            VramBytes = core.VramBytes,
            PaletteBearing = obs.Plan.Content.Any(c => c.Requirement == ContentRequirement.MayUse),
            MustStateBearing = obs.Plan.Content.Any(c => c.Requirement == ContentRequirement.MustState),
            QuestionMode = obs.Plan.Question is null
                ? "none" : obs.Plan.Question.Mandatory ? "mandatory" : "optional",
            ShadowViolations = shadowViolations,
            ProductionViolations = productionViolations,
            ShadowSludge = RendererShadowChecks.Sludge(core.Reply),
            ProductionSludge = RendererShadowChecks.Sludge(obs.ProductionResponse),
            UserMessage = obs.UserMessage,
            Plan2 = core.Plan2,
        });

        await _recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = RendererShadowSubject,
            UserId = obs.UserId,
            SourceMessageId = obs.SourceMessageId,
            ConversationId = obs.ConversationId,
            Legacy = obs.ProductionResponse,
            Model = core.Reply,
            Confidence = 0,

            // "Agreed" here means the shadow reply passed every deterministic check — the
            // property the promotion decision counts, stored so the existing agreement
            // endpoint reports the violation rate without parsing envelopes.
            Agreed = shadowViolations.Count == 0,

            // Which reply the user actually saw: "legacy" for shadow rows and canary
            // fallbacks, "model" when the canary displayed the tuned renderer.
            Applied = applied,
            DurationMs = core.LatencyMs,
            Input = envelope,
        }, ct);
    }

    /// <summary>
    /// P2 (docs/RESPONSE_PLAN_V3_SPEC.md §9): the plan takes the v3 producer hop —
    /// FromV2 → guarded TranslateToV2 — before the frozen serializer. Byte-identity is
    /// proven corpus-wide by golden tests (804/804); this guard makes the property
    /// load-bearing at runtime too: any divergence or hop failure falls back to the
    /// direct serialization, logged, so run-1c behavior cannot change. V3 stays
    /// non-authoritative: its output is only ever the identical bytes.
    /// </summary>
    private string SerializeViaV3Hop(ResponsePlan plan)
    {
        var direct = PlanSerialization.CompactV2(plan);
        try
        {
            var v3 = Companion.PlanV3.V2Translation.FromV2(plan);
            var hop = PlanSerialization.CompactV2(Companion.PlanV3.V2Translation.TranslateToV2(v3));
            if (string.Equals(hop, direct, StringComparison.Ordinal))
                return hop;
            _logger.LogWarning("V3 hop diverged from direct CompactV2 for {TraceId}; using direct.", plan.TraceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "V3 hop failed for {TraceId}; using direct CompactV2.", plan.TraceId);
        }
        return direct;
    }

    /// <summary>
    /// P3 (docs/RESPONSE_PLAN_V3_SPEC.md §14): records the translated_v2 V3 envelope
    /// beside the plan2 row. CompactV3 is never sent to a model; protected text never
    /// enters the row (the builder applies the complete disclosure/retention rules);
    /// unknown extensions and invalid plans record names/reasons only.
    /// </summary>
    private async Task RecordV3RowAsync(RendererShadowObservation obs, CancellationToken ct)
    {
        if (!_recorder.IsRecording)
            return;

        Interlocked.Increment(ref _v3Produced);
        var v3 = V2Translation.FromV2(obs.Plan);

        byte[]? key = null;
        if (!string.IsNullOrEmpty(_options.CorrelationKeyBase64))
        {
            try { key = Convert.FromBase64String(_options.CorrelationKeyBase64); }
            catch (FormatException) { _logger.LogWarning("CorrelationKeyBase64 is not valid base64; tags disabled."); }
        }

        var userIds = v3.Participants
            .Where(pt => pt.Role == ParticipantRole.user)
            .Select(pt => pt.Id)
            .ToList();
        var trust = new RendererTrustContext(RendererTransport.local_loopback);
        var envelope = V3ShadowEnvelopeBuilder.Build(
            obs.Plan, v3, key, _options.CorrelationKeyVersion, userIds, trust);

        // P4: the native_v3 sibling and its semantic parity, recorded in the same row.
        envelope = V3ShadowEnvelopeBuilder.WithNative(
            envelope, v3, obs.NativeV3, obs.NativeBuildError, obs.NativeLintRejections,
            key, _options.CorrelationKeyVersion, userIds, trust);
        if (obs.NativeV3 is not null)
        {
            Interlocked.Increment(ref _v3NativeBuilt);
            if (envelope.Parity.All(pc => pc.Status is "match" or "incomparable-prose"))
                Interlocked.Increment(ref _v3NativeParityMatch);
            else
                Interlocked.Increment(ref _v3NativeParityDiffers);
        }
        else
        {
            Interlocked.Increment(ref _v3NativeBuildFailed);
        }
        if (obs.NativeLintRejections.Count > 0)
            Interlocked.Add(ref _v3NativeLintRejects, obs.NativeLintRejections.Count);

        // P5/Source 2: the contribution-boundary diagnostics for whatever was folded into
        // the native plan. Content-safe by construction — ids, decisions, reasons, counts.
        if (obs.NativeAssembly is { } assembly)
            envelope = V3ShadowEnvelopeBuilder.WithAssembly(envelope, assembly);

        // plan/4 evidence: a transition token and a size. No plan/4 text is stored or sent.
        if (obs.NativeFrameTransition is not null || obs.NativeCompactV4Chars is not null)
            envelope = envelope with
            {
                FrameTransition = obs.NativeFrameTransition,
                CompactV4Chars = obs.NativeCompactV4Chars,
            };

        Interlocked.Increment(ref envelope.Valid ? ref _v3Valid : ref _v3Invalid);
        if (envelope.V2Compatible) Interlocked.Increment(ref _v3Compatible);
        if (envelope.ContainsProtected) Interlocked.Increment(ref _v3Protected);
        if (envelope.RedactedItemCount > 0) Interlocked.Increment(ref _v3Redacted);

        await _recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = RendererV3Subject,
            UserId = obs.UserId,
            SourceMessageId = obs.SourceMessageId,
            ConversationId = obs.ConversationId,
            Legacy = null,
            Model = null,
            Confidence = 0,
            Agreed = envelope.Valid,
            Applied = "none",
            DurationMs = 0,
            Input = JsonSerializer.Serialize(envelope),
        }, ct);
    }

    /// <summary>Subject key for renderer rows; the forget path and the report tooling both key on it.</summary>
    public const string RendererShadowSubject = "renderer.plan2";

    /// <summary>P3 v3-envelope rows; swept by the same renderer.* forget clause.</summary>
    public const string RendererV3Subject = "renderer.plan3";
}

/// <summary>The structured half of a renderer shadow row, stored as JSON in the Input column.</summary>
public sealed record RendererShadowEnvelope
{
    public required string PlanHash { get; init; }
    public required string AdapterSha256 { get; init; }
    public required string ModelVersion { get; init; }
    public required long LatencyMs { get; init; }
    public required long VramBytes { get; init; }
    public required bool PaletteBearing { get; init; }
    public required bool MustStateBearing { get; init; }
    public required string QuestionMode { get; init; }
    public required List<string> ShadowViolations { get; init; }
    public required List<string> ProductionViolations { get; init; }
    public required List<string> ShadowSludge { get; init; }
    public required List<string> ProductionSludge { get; init; }
    public required string UserMessage { get; init; }
    public required string Plan2 { get; init; }
}
