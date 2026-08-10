using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Companion.Api;

/// <summary>
/// Gives the companion its own clock: a background loop that runs the full sleep cycle — the
/// reflection pass (inner monologue) plus memory housekeeping — when the user has been away for a
/// while, instead of only ever thinking when spoken to. Mirrors how reflection actually works —
/// you think a conversation over <em>after</em> it ends — and keeps the model call entirely off
/// any request path.
///
/// Cheap when idle: each tick is one timestamp query; the reflector's own min-material guard
/// decides whether a model call is worth it. Attempts are spaced at least one idle-window apart,
/// so a failing model (or the offline mock, whose prose never parses as a reflection) never
/// spams retries.
/// </summary>
public sealed class ReflectionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IUserContext _user;
    private readonly IOptions<CompanionOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReflectionWorker> _logger;

    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public ReflectionWorker(
        IServiceScopeFactory scopes,
        IUserContext user,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<ReflectionWorker> logger)
    {
        _scopes = scopes;
        _user = user;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.EnableReflection)
            return;

        var checkEvery = TimeSpan.FromMinutes(Math.Max(1, options.ReflectionCheckMinutes));
        using var timer = new PeriodicTimer(checkEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The loop must survive a bad pass; the next tick simply tries again.
                    _logger.LogError(ex, "Reflection tick failed; will retry on a later tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — a normal way for the loop to end.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var now = _clock.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.ReflectionIdleMinutes);

        // Space attempts at least one idle-window apart (also the retry backoff after a failure).
        if (now - _lastAttempt < idle)
            return;

        using var scope = _scopes.CreateScope();

        // Reflection happens after the conversation, not during: only when the user has actually
        // been away for the idle window. While they're around, every tick is this one cheap query.
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var lastSeen = await conversations.GetLastMessageAtAsync(_user.UserId, ct);
        if (lastSeen is null || now - lastSeen < idle)
            return;

        _lastAttempt = now;

        // The full sleep, not just the thought: reflect, then consolidate and tidy curiosities.
        // The cycle logs its own outcome.
        await scope.ServiceProvider.GetRequiredService<ISleepCycle>().RunAsync(_user.UserId, ct);
    }
}
