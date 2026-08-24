using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
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
    private readonly Channel<RendererShadowObservation> _queue;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IShadowRecorder _recorder;
    private readonly RendererShadowOptions _options;
    private readonly ILogger<RendererShadowService> _logger;

    private long _queued;
    private long _completed;
    private long _failed;
    private long _dropped;

    public RendererShadowService(
        IShadowRecorder recorder,
        IOptions<CompanionOptions> options,
        ILogger<RendererShadowService> logger)
        : this(recorder, options, logger, drainWindow: null)
    {
    }

    /// <summary>Test seam: a short drain window keeps shutdown tests fast. Behavior is identical.</summary>
    internal RendererShadowService(
        IShadowRecorder recorder,
        IOptions<CompanionOptions> options,
        ILogger<RendererShadowService> logger,
        TimeSpan? drainWindow)
    {
        _drainWindow = drainWindow ?? TimeSpan.FromSeconds(45);
        _recorder = recorder;
        _options = options.Value.RendererShadow;
        _logger = logger;
        _queue = Channel.CreateBounded<RendererShadowObservation>(new BoundedChannelOptions(QueueCapacity)
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
        _queue.Reader.Count);

    public void Observe(RendererShadowObservation observation)
    {
        if (!IsObserving)
            return;

        if (_queue.Writer.TryWrite(observation))
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
        await foreach (var observation in _queue.Reader.ReadAllAsync(CancellationToken.None))
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
        var plan2 = PlanSerialization.CompactV2(obs.Plan);
        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan2)))
            .ToLowerInvariant();
        var userPrompt = PlanSerialization.BuildUserPrompt(
            "v2", obs.Plan, obs.Transcript, obs.UserMessage);

        var payload = new
        {
            model = "renderer-shadow",
            stream = false,
            options = new { temperature = 0.6, num_predict = 220 },
            messages = new object[]
            {
                new { role = "system", content = PlanSerialization.SystemPromptV2 },
                new { role = "user", content = userPrompt },
            },
        };

        var started = Stopwatch.GetTimestamp();
        using var response = await Http.PostAsync(
            $"{_options.Endpoint.TrimEnd('/')}/api/chat",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var shadowReply = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        var latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        long vramBytes = 0;
        try
        {
            using var ps = await Http.GetAsync($"{_options.Endpoint.TrimEnd('/')}/api/ps", ct);
            using var psDoc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync(ct));
            vramBytes = psDoc.RootElement.GetProperty("models")[0].GetProperty("size_vram").GetInt64();
        }
        catch (Exception)
        {
            // VRAM is nice-to-have telemetry; its absence never costs the row.
        }

        var shadowViolations = RendererShadowChecks.Score(obs.Plan, shadowReply);
        var productionViolations = RendererShadowChecks.Score(obs.Plan, obs.ProductionResponse);

        var envelope = JsonSerializer.Serialize(new RendererShadowEnvelope
        {
            PlanHash = planHash,
            AdapterSha256 = _options.AdapterSha256,
            ModelVersion = _options.ModelVersion,
            LatencyMs = latencyMs,
            VramBytes = vramBytes,
            PaletteBearing = obs.Plan.Content.Any(c => c.Requirement == ContentRequirement.MayUse),
            MustStateBearing = obs.Plan.Content.Any(c => c.Requirement == ContentRequirement.MustState),
            QuestionMode = obs.Plan.Question is null
                ? "none" : obs.Plan.Question.Mandatory ? "mandatory" : "optional",
            ShadowViolations = shadowViolations,
            ProductionViolations = productionViolations,
            ShadowSludge = RendererShadowChecks.Sludge(shadowReply),
            ProductionSludge = RendererShadowChecks.Sludge(obs.ProductionResponse),
            UserMessage = obs.UserMessage,
            Plan2 = plan2,
        });

        await _recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = RendererShadowSubject,
            Legacy = obs.ProductionResponse,
            Model = shadowReply,
            Confidence = 0,

            // "Agreed" here means the shadow reply passed every deterministic check — the
            // property the promotion decision counts, stored so the existing agreement
            // endpoint reports the violation rate without parsing envelopes.
            Agreed = shadowViolations.Count == 0,
            Applied = "legacy",
            DurationMs = latencyMs,
            Input = envelope,
        }, ct);
    }

    /// <summary>Subject key for renderer rows; the forget path and the report tooling both key on it.</summary>
    public const string RendererShadowSubject = "renderer.plan2";
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
