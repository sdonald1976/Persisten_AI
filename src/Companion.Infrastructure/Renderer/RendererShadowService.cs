using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
///  1. <see cref="Observe"/> returns before any network or model work happens; the caller's
///     turn cannot be delayed, and the observation record is an immutable snapshot, so the
///     shadow path has no handle back into conversation state, memory, goals, or tools.
///  2. Everything downstream is wrapped: a dead endpoint, a timeout, a serialization bug —
///     each is a debug/warning log line and a dropped data point, never anything more.
///  3. The shadow reply is stored in the shadow-comparison table only, under the same
///     retention and forget rules as every other captured sentence.
/// </summary>
public sealed class RendererShadowService : IRendererShadow
{
    private static readonly HttpClient Http = new();

    private readonly IShadowRecorder _recorder;
    private readonly RendererShadowOptions _options;
    private readonly ILogger<RendererShadowService> _logger;

    public RendererShadowService(
        IShadowRecorder recorder,
        IOptions<CompanionOptions> options,
        ILogger<RendererShadowService> logger)
    {
        _recorder = recorder;
        _options = options.Value.RendererShadow;
        _logger = logger;
    }

    public bool IsObserving => _options.Enabled && _recorder.IsRecording;

    public void Observe(RendererShadowObservation observation)
    {
        if (!IsObserving)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)));
                await RenderAndRecordAsync(observation, cts.Token);
            }
            catch (Exception ex)
            {
                // The whole point of a shadow: being wrong here is data, being loud here is a bug.
                _logger.LogDebug(ex, "Renderer shadow observation dropped for {TraceId}.", observation.TraceId);
            }
        });
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
