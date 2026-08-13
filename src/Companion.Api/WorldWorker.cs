using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.World;

namespace Companion.Api;

/// <summary>
/// Keeps her in her world: holds the connection open, and every so often asks whether she would
/// rather be somewhere else.
///
/// The division of labour matters here. This worker owns *when* to reconsider; `RoamingPolicy`
/// owns *what* she decides, from her own state and the menu the world last advertised. Nothing in
/// either knows the names of any rooms.
///
/// It reconsiders far more often than she moves. The policy mostly answers "stay where you are",
/// which is what a person does — getting up needs a reason, and a companion who relocated every
/// two minutes would read as agitated rather than alive.
/// </summary>
public sealed class WorldWorker : BackgroundService
{
    private readonly IWorldLink _link;
    private readonly WorldOptions _options;
    private readonly IUserContext _user;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorldWorker> _logger;

    private string? _previousPlace;

    public WorldWorker(
        IWorldLink link, WorldOptions options, IUserContext user, IServiceScopeFactory scopes,
        TimeProvider clock, ILogger<WorldWorker> logger)
    {
        _link = link;
        _options = options;
        _user = user;
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_link.Configured)
            return;

        _link.Perceived += OnPerceived;

        if (_link is WebSocketWorldLink live)
            live.Start();

        var period = TimeSpan.FromMinutes(Math.Max(0.25, _options.RoamCheckMinutes));
        using var timer = new PeriodicTimer(period);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await ConsiderMovingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deciding where to be failed; she stays where she is.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ConsiderMovingAsync(CancellationToken ct)
    {
        if (!_options.Roam || !_link.Connected)
            return;

        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        var state = await sp.GetRequiredService<ICompanionStateTracker>()
            .BuildAsync(_user.UserId, ct);

        // What is on her mind: the things she is genuinely wondering about. This is the signal
        // that makes a move legible — going to the greenhouse because she is curious about the
        // greenhouse is a reason a person would give.
        var curiosities = await sp.GetRequiredService<IReflectionStore>()
            .GetOpenCuriositiesAsync(_user.UserId, ct);
        var preoccupations = curiosities
            .Select(c => $"{c.About} {c.Question}".Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var choice = RoamingPolicy.Choose(
            _link.Places, _link.CurrentPlace, _previousPlace, state, preoccupations);

        if (choice is null)
            return;

        var from = _link.CurrentPlace;
        if (await _link.GoToAsync(choice.PlaceId, ct))
        {
            _previousPlace = from;
            _logger.LogInformation(
                "She's heading to the {Place} — {Reason}.", choice.PlaceId, choice.Reason);
        }
    }

    /// <summary>
    /// Records what happened to her, so reflection can read it later.
    ///
    /// It goes to her experience log and nowhere near the memory store. A world event is not a
    /// fact about the user's life — an afternoon in the greenhouse says nothing about them — and
    /// the separation is structural rather than a rule someone has to remember.
    ///
    /// Fire-and-forget: perception must never stall the connection, and a lost line is a lost
    /// line rather than a broken world.
    /// </summary>
    private void OnPerceived(WorldPerception perception)
    {
        _logger.LogInformation("World: {Text}", perception.Text);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IExperienceStore>().AddAsync(
                    new Experience
                    {
                        Id = Guid.NewGuid(),
                        UserId = _user.UserId,
                        At = perception.At,
                        Source = "world",
                        Kind = perception.Kind,
                        Text = perception.Text,
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record a world experience.");
            }
        });
    }
}
