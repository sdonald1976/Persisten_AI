using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Companion.Api;

/// <summary>
/// The timer behind the companion reaching out on her own. Each tick asks
/// <see cref="IOutreachService"/> to consider one outreach; every judgment about whether a
/// message should actually go (away-gate, budget, quiet hours, having something real to say)
/// lives in the service, so this stays a dumb clock. Exits immediately when no channel is
/// configured — the feature is off until Outreach.NtfyUrl is set.
/// </summary>
public sealed class OutreachWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IUserContext _user;
    private readonly IOutboundChannel _channel;
    private readonly IOptions<OutreachOptions> _options;
    private readonly ILogger<OutreachWorker> _logger;

    public OutreachWorker(
        IServiceScopeFactory scopes,
        IUserContext user,
        IOutboundChannel channel,
        IOptions<OutreachOptions> options,
        ILogger<OutreachWorker> logger)
    {
        _scopes = scopes;
        _user = user;
        _channel = channel;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_channel.Configured)
            return;

        var checkEvery = TimeSpan.FromMinutes(Math.Max(1, _options.Value.CheckMinutes));
        using var timer = new PeriodicTimer(checkEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IOutreachService>()
                        .RunOnceAsync(_user.UserId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Outreach check failed; will retry on a later tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}
