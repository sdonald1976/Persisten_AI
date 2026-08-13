using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.World;

/// <summary>
/// The companion's end of the world's wire.
///
/// Everything here is connection and translation. It holds what the world last said and forgets it
/// the moment the connection drops, which is the point: the design forbids the companion keeping a
/// model of somewhere else, and the only reliable way to honour that is to have nowhere to put one.
///
/// A world that is absent, unreachable, or restarting is a normal state. She simply isn't anywhere,
/// and nothing else in the companion changes.
/// </summary>
public sealed class WebSocketWorldLink : IWorldLink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly WorldOptions _options;
    private readonly ILogger<WebSocketWorldLink> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _sending = new(1, 1);

    private ClientWebSocket? _socket;
    private Task? _pump;
    private int _disposed;
    private volatile IReadOnlyList<WorldPlace> _places = Array.Empty<WorldPlace>();

    public WebSocketWorldLink(WorldOptions options, ILogger<WebSocketWorldLink> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool Configured => _options.Configured;

    public bool Connected => _socket?.State == WebSocketState.Open && _places.Count > 0;

    public IReadOnlyList<WorldPlace> Places => _places;

    public string? CurrentPlace { get; private set; }

    public event Action<WorldPerception>? Perceived;

    /// <summary>Starts connecting, and keeps reconnecting for as long as the companion runs.</summary>
    public void Start()
    {
        if (!Configured)
        {
            _logger.LogInformation("No world configured; she isn't anywhere.");
            return;
        }

        _pump = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync();
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "World connection ended.");
            }

            // Forget everything the world told us. A stale menu is the beginning of a model.
            _places = Array.Empty<WorldPlace>();
            CurrentPlace = null;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectSeconds)), _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAndListenAsync()
    {
        using var socket = new ClientWebSocket();
        _socket = socket;

        await socket.ConnectAsync(new Uri(_options.Url), _stopping.Token);
        await SendAsync(new { type = "auth", token = _options.Token });

        _logger.LogInformation("Connected to her world at {Url}.", _options.Url);

        var buffer = new byte[32 * 1024];
        while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, _stopping.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Her world closed the connection.");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Handle(Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    /// <summary>
    /// Translates one world message. Anything unrecognised is ignored rather than fatal — the
    /// world is a separate application and may learn to say things this version has never heard.
    /// </summary>
    private void Handle(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            _logger.LogDebug("Her world said something unparseable.");
            return;
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var at = root.TryGetProperty("at", out var a) && a.TryGetDateTimeOffset(out var stamp)
            ? stamp
            : DateTimeOffset.UtcNow;

        switch (type)
        {
            case "hello":
                _places = ReadPlaces(root);
                CurrentPlace = root.TryGetProperty("place", out var p) ? p.GetString() : null;
                _logger.LogInformation(
                    "Her world has {Count} places; she is in the {Place}.",
                    _places.Count, CurrentPlace ?? "(nowhere yet)");
                break;

            case "arrived":
            {
                var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                var place = root.TryGetProperty("place", out var pl) ? pl.GetString() : null;
                if (IsHer(body))
                    CurrentPlace = place;
                Raise(new WorldPerception(at, "arrived", body, place,
                    IsHer(body) ? $"You went to the {place}." : $"{body} went to the {place}."));
                break;
            }

            case "presence":
            {
                var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
                var place = root.TryGetProperty("place", out var pl) ? pl.GetString() : null;
                Raise(new WorldPerception(at, "presence", body, place,
                    state == "joined" ? $"{body} came into the world." : $"{body} left."));
                break;
            }

            case "refusal":
            {
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : "refused";
                var text = root.TryGetProperty("message", out var m) ? m.GetString() : "";
                // "acknowledged" is the world agreeing, not objecting.
                if (code != "acknowledged")
                    _logger.LogWarning("Her world refused something: {Code} — {Message}", code, text);
                Raise(new WorldPerception(at, "refusal", null, null, text ?? ""));
                break;
            }
        }
    }

    private static bool IsHer(string? body) => string.Equals(body, "ava", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<WorldPlace> ReadPlaces(JsonElement root)
    {
        if (!root.TryGetProperty("places", out var places) || places.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorldPlace>();

        var list = new List<WorldPlace>();
        foreach (var place in places.EnumerateArray())
        {
            var id = place.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            list.Add(new WorldPlace(
                id,
                place.TryGetProperty("name", out var n) ? n.GetString() ?? id : id,
                place.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
        }
        return list;
    }

    private void Raise(WorldPerception perception)
    {
        try
        {
            Perceived?.Invoke(perception);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A world perception handler threw; ignoring it.");
        }
    }

    public async Task<bool> GoToAsync(string placeId, CancellationToken ct = default)
    {
        if (_socket?.State != WebSocketState.Open)
            return false;

        // Only somewhere the world said exists. Sending a name it never advertised would mean the
        // companion had decided it knows the layout better than the world does.
        if (!_places.Any(p => string.Equals(p.Id, placeId, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Asked to send her to '{Place}', which her world has not mentioned.", placeId);
            return false;
        }

        await SendAsync(new { type = "goto", place = placeId });
        return true;
    }

    private async Task SendAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));

        await _sending.WaitAsync(_stopping.Token);
        try
        {
            if (_socket?.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stopping.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // The world went away mid-send. The reconnect loop will notice.
        }
        finally
        {
            _sending.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposal must be safe to repeat. A host that shuts down and is disposed again — which
        // the API test factory does routinely — otherwise gets an ObjectDisposedException from the
        // cancellation source, turning an orderly shutdown into a failure.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _stopping.CancelAsync();
        try
        {
            if (_socket?.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch
        {
            // Shutting down.
        }

        if (_pump is not null)
        {
            try { await _pump; } catch { /* shutting down */ }
        }

        _stopping.Dispose();
        _sending.Dispose();
    }
}
