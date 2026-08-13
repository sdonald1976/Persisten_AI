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
    private volatile IReadOnlyList<WorldConcern> _concerns = Array.Empty<WorldConcern>();

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
            _concerns = Array.Empty<WorldConcern>();
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
                _concerns = ReadConcerns(root);
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

            case "noticed":
            {
                var place = root.TryGetProperty("place", out var pl) ? pl.GetString() : null;
                var thing = root.TryGetProperty("thing", out var th) ? th.GetString() : null;
                var text = root.TryGetProperty("text", out var tx) ? tx.GetString() : null;

                // The menu she was handed on connecting is a snapshot. Things change afterwards,
                // and without folding those changes in here she would only ever act on what was
                // true the moment she arrived — which for a world that changes over hours is
                // almost never.
                if (!string.IsNullOrWhiteSpace(thing) && !string.IsNullOrWhiteSpace(place))
                {
                    var needs = root.TryGetProperty("needsAttention", out var n) && n.ValueKind == JsonValueKind.True;
                    var condition = root.TryGetProperty("condition", out var c) ? c.GetString() ?? "" : "";
                    UpdateConcern(thing!, place!, condition, text ?? "", needs);
                }

                if (!string.IsNullOrWhiteSpace(text))
                    Raise(new WorldPerception(at, "noticed", thing, place, text!));
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

    /// <summary>
    /// Folds one change into what she knows wants attention. Still not a stored model: this is the
    /// running total of what the world has said since it connected, and it goes when it does.
    /// </summary>
    private void UpdateConcern(
        string thingId, string placeId, string condition, string text, bool needsAttention)
    {
        var without = _concerns
            .Where(c => !string.Equals(c.ThingId, thingId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (needsAttention)
        {
            // Keep the name from the menu if we have it, so it still reads as a thing rather than
            // an identifier.
            var name = _concerns
                .FirstOrDefault(c => string.Equals(c.ThingId, thingId, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? thingId;
            without.Add(new WorldConcern(
                thingId, placeId, name, condition,
                string.IsNullOrWhiteSpace(text) ? $"{name} needs attention" : text));
        }

        _concerns = without;
    }

    /// <summary>
    /// What the world says wants looking after, read straight out of the menu it just sent. Like
    /// the places themselves, this is never stored — it is what she was told, not what she knows.
    /// </summary>
    private static IReadOnlyList<WorldConcern> ReadConcerns(JsonElement root)
    {
        if (!root.TryGetProperty("places", out var places) || places.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorldConcern>();

        var concerns = new List<WorldConcern>();
        foreach (var place in places.EnumerateArray())
        {
            var placeId = place.TryGetProperty("id", out var pid) ? pid.GetString() : null;
            if (string.IsNullOrWhiteSpace(placeId))
                continue;
            if (!place.TryGetProperty("things", out var things) || things.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var thing in things.EnumerateArray())
            {
                var needs = thing.TryGetProperty("needsAttention", out var n) && n.ValueKind == JsonValueKind.True;
                if (!needs)
                    continue;

                var id = thing.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = thing.TryGetProperty("name", out var nm) ? nm.GetString() ?? id : id;
                var condition = thing.TryGetProperty("condition", out var c) ? c.GetString() ?? "" : "";
                var text = thing.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                concerns.Add(new WorldConcern(
                    id!, placeId!, name!, condition, text ?? $"{name} needs attention"));
            }
        }
        return concerns;
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

    public IReadOnlyList<WorldConcern> Concerns => _concerns;

    public async Task<bool> TendAsync(string thingId, CancellationToken ct = default)
    {
        if (_socket?.State != WebSocketState.Open)
            return false;

        await SendAsync(new { type = "tend", thing = thingId });
        return true;
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
