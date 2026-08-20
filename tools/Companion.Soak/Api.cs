using System.Net.Http.Json;
using System.Text.Json;

namespace Companion.Soak;

/// <summary>One turn as the harness saw it: what was said, what came back, what it cost — and,
/// from the companion's own diagnostics, what the system decided along the way. The decision
/// fields let a scenario assert on what the ARCHITECTURE did, not just on the reply's prose.</summary>
public sealed record Turn(
    string Sent, string Reply, int PacketTokens, int? PromptTokens, int Rounds, TimeSpan Took,
    string? TraceId = null,
    IReadOnlyList<string>? Sections = null,
    IReadOnlyList<string>? Decisions = null,
    string? WorkingContext = null,
    IReadOnlyList<string>? Retrieved = null,
    IReadOnlyList<string>? RetrievedRaw = null);

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
        var last = await LastTurnAsync();
        return new Turn(message, reply, last.Packet, null, last.Rounds, took,
            last.TraceId, last.Sections, last.Decisions,
            last.WorkingContext, last.Retrieved, last.RetrievedRaw);
    }

    private sealed record LastTurn(
        int Packet, int Rounds, string? TraceId,
        IReadOnlyList<string> Sections, IReadOnlyList<string> Decisions,
        string? WorkingContext, IReadOnlyList<string> Retrieved, IReadOnlyList<string> RetrievedRaw);

    /// <summary>The most recent turn's cost and decisions, from the companion's own diagnostics.
    /// Decisions are flattened to "stage=verdict" strings — enough for a scenario to assert on.</summary>
    private async Task<LastTurn> LastTurnAsync()
    {
        var empty = new LastTurn(0, 0, null, Array.Empty<string>(), Array.Empty<string>(),
            null, Array.Empty<string>(), Array.Empty<string>());
        try
        {
            var turns = await _http.GetFromJsonAsync<JsonElement>("/diagnostics/turns");
            if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
                return empty;

            var last = turns[0];
            var packet = last.TryGetProperty("packetTokens", out var p) ? p.GetInt32() : 0;
            var rounds = last.TryGetProperty("generationRounds", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32() : 0;
            var traceId = last.TryGetProperty("traceId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
            var sections = Strings(last, "contextSections");
            var decisions = last.TryGetProperty("decisions", out var ds) && ds.ValueKind == JsonValueKind.Array
                ? ds.EnumerateArray().Select(d =>
                        (d.TryGetProperty("stage", out var st) ? st.GetString() : null) + "=" +
                        (d.TryGetProperty("verdict", out var v) ? v.GetString() : null))
                    .ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();
            return new LastTurn(packet, rounds, traceId, sections, decisions,
                DescribeWorkingContext(last),
                Strings(last, "retrievedSummaries"),
                Strings(last, "retrievedWithRawQuery"));
        }
        catch (Exception)
        {
            return empty;
        }
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
        => e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The turn's working-context state, flattened to one readable line.</summary>
    private static string? DescribeWorkingContext(JsonElement turn)
    {
        if (!turn.TryGetProperty("workingContext", out var wc) || wc.ValueKind != JsonValueKind.Object)
            return null;

        string? Str(string name) => wc.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

        var open = wc.TryGetProperty("openQuestions", out var oq) && oq.ValueKind == JsonValueKind.Array
            ? oq.EnumerateArray()
                .Select(q => q.TryGetProperty("question", out var qt) ? qt.GetString() : null)
                .Where(q => !string.IsNullOrEmpty(q)).ToList()
            : new List<string?>();
        var markers = wc.TryGetProperty("referenceMarkers", out var rm) && rm.ValueKind == JsonValueKind.Array
            ? string.Join(",", rm.EnumerateArray().Select(m => m.GetString()))
            : "";

        var parts = new List<string> { $"move={Str("move")}" };
        if (Str("topic") is { } topic) parts.Add($"topic=\"{topic}\"");
        if (markers.Length > 0) parts.Add($"markers=[{markers}]");
        if (Str("resolvedReference") is { } referent) parts.Add($"ref->\"{referent}\"");
        if (Str("boundQuestion") is { } bound) parts.Add($"answers=\"{bound}\"");
        if (open.Count > 0) parts.Add($"open=[{string.Join(" | ", open)}]");
        var raw = Str("rawQuery");
        var query = Str("retrievalQuery");
        if (raw is not null && query is not null && raw != query)
            parts.Add($"query: \"{raw}\" -> \"{query}\"");
        return string.Join("  ", parts);
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

    /// <summary>
    /// Every memory with the status the store gave it. The status is the point: a fact the user
    /// changed should be Superseded and a fact they denied should be Disputed, and reading only the
    /// text cannot tell a store that quietly kept two contradictory facts from one that revised.
    /// </summary>
    public async Task<IReadOnlyList<(string Content, string Status)>> MemoryStatesAsync()
    {
        try
        {
            var mem = await _http.GetFromJsonAsync<JsonElement>("/memories");
            if (mem.ValueKind != JsonValueKind.Array)
                return Array.Empty<(string, string)>();

            return mem.EnumerateArray()
                .Select(m => (
                    Content: m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    Status: m.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""))
                .Where(x => x.Content.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>What she currently thinks is unfinished.</summary>
    public async Task<IReadOnlyList<string>> OpenLoopsAsync()
    {
        try
        {
            var loops = await _http.GetFromJsonAsync<JsonElement>("/loops");
            if (loops.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return loops.EnumerateArray()
                .Select(l => l.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")
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
