using System.Net.Http.Json;
using System.Text.Json;

namespace Companion.Soak;

/// <summary>One turn as the harness saw it: what was said, what came back, and what it cost.</summary>
public sealed record Turn(string Sent, string Reply, int PacketTokens, int? PromptTokens, int Rounds, TimeSpan Took);

/// <summary>
/// The companion as a caller meets it — over HTTP, with no access to its internals.
///
/// Deliberately not a reference to Companion.Core. Every bug this harness exists to catch lived in
/// the seams between the pieces: a model role pointed at a model that cannot do the job, a prompt
/// that overflowed a window nothing knew the size of, a filter that never ran on the path a person
/// actually uses. A harness that reached inside would share the same wiring, and share the blind
/// spot with it.
/// </summary>
public sealed class Api
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public Api(string baseUrl, TimeSpan timeout)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = timeout };
    }

    public async Task<bool> HealthyAsync()
    {
        try
        {
            using var res = await _http.GetAsync("/health");
            return res.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<string> StartConversationAsync(string title)
    {
        using var res = await _http.PostAsJsonAsync("/conversations", new { title, source = "soak" }, Json);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("conversationId").GetString()!;
    }

    public async Task<Turn> SayAsync(string conversationId, string message)
    {
        var started = DateTimeOffset.UtcNow;
        using var res = await _http.PostAsJsonAsync("/chat", new { conversationId, message }, Json);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var took = DateTimeOffset.UtcNow - started;

        var reply = doc.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var (packet, prompt, rounds) = await LastTurnCostAsync();
        return new Turn(message, reply, packet, prompt, rounds, took);
    }

    /// <summary>What the most recent turn cost, from the companion's own diagnostics.</summary>
    private async Task<(int Packet, int? Prompt, int Rounds)> LastTurnCostAsync()
    {
        try
        {
            var turns = await _http.GetFromJsonAsync<JsonElement>("/diagnostics/turns");
            if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
                return (0, null, 0);

            var last = turns[0];
            var packet = last.TryGetProperty("packetTokens", out var p) ? p.GetInt32() : 0;
            var rounds = last.TryGetProperty("generationRounds", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32() : 0;
            return (packet, null, rounds);
        }
        catch (Exception)
        {
            return (0, null, 0);
        }
    }

    public async Task<int> MemoryCountAsync()
    {
        try
        {
            var mem = await _http.GetFromJsonAsync<JsonElement>("/memories");
            return mem.ValueKind == JsonValueKind.Array ? mem.GetArrayLength() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public async Task<IReadOnlyList<string>> MemoriesAsync()
    {
        try
        {
            var mem = await _http.GetFromJsonAsync<JsonElement>("/memories");
            if (mem.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return mem.EnumerateArray()
                .Select(m => m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>How many memories the last turn actually pulled into the prompt.</summary>
    public async Task<int> LastTurnMemoriesRetrievedAsync()
    {
        try
        {
            var turns = await _http.GetFromJsonAsync<JsonElement>("/diagnostics/turns");
            if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
                return 0;
            return turns[0].TryGetProperty("memoriesRetrieved", out var m) ? m.GetInt32() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
