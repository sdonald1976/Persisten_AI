using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;

namespace Companion.Api;

/// <summary>
/// Runs the model preflight once, shortly after startup, and files the result where the rest of
/// the app can see it: the log (one actionable line per missing model), <c>/health</c>, and the
/// capability registry.
///
/// Deliberately a background task rather than startup work — a wrong or un-pulled model name
/// shouldn't stop the API from serving, and a provider that is slow to answer shouldn't hold the
/// port closed. The window where <c>/capabilities</c> hasn't been corrected yet is a second or two.
/// </summary>
public sealed class ModelPreflightWorker : BackgroundService
{
    private readonly ModelPreflight _preflight;
    private readonly ModelPreflightState _state;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ModelPreflightWorker> _logger;

    public ModelPreflightWorker(
        ModelPreflight preflight, ModelPreflightState state, IServiceScopeFactory scopes,
        TimeProvider clock, ILogger<ModelPreflightWorker> logger)
    {
        _preflight = preflight;
        _state = state;
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var report = await _preflight.RunAsync(_clock.GetUtcNow(), stoppingToken);
            _state.Report = report;

            // Only models the probe reached a verdict on. "Unreachable" stays absent from the map
            // so it can't be mistaken for "missing" downstream.
            var presence = report.Roles
                .Where(r => r.Present is not null && !string.IsNullOrWhiteSpace(r.Model))
                .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.All(r => r.Present == true), StringComparer.OrdinalIgnoreCase);
            if (presence.Count == 0)
                return;

            using var scope = _scopes.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ICapabilityRegistry>();
            await registry.ApplyModelProbeAsync(presence, _clock.GetUtcNow(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Preflight is diagnostics: it must never be the reason the companion doesn't start.
            _logger.LogWarning(ex, "Model preflight did not complete; capability availability is unverified.");
        }
    }
}
