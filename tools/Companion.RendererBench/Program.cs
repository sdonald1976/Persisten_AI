using System.Text;
using System.Text.Json;
using Companion.Core.Domain;
using Companion.RendererBench;

// The Language Organ bench (docs/RENDERER.md): sends the IDENTICAL serialized
// ResponsePlan to multiple candidate models and scores each utterance against the
// deterministic fidelity contract. Offline, read-only, touches no production data —
// it talks straight to Ollama. This is the no-training baseline that can falsify the
// Language Organ idea before anything is trained.

var fixturesPath = Args("--fixtures") ?? @"training\renderer\fixtures.jsonl";
var ollama = Args("--ollama") ?? "http://localhost:11434";
var models = (Args("--models") ??
    "qwen3:0.6b,llama3.2:1b,qwen2.5:1.5b-instruct,qwen2.5:3b-instruct,llama3.2:3b," +
    "hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M,qwen3:8b").Split(',');
var outPath = Args("--out") ?? @"training\renderer\baseline-results.md";
var serialization = Args("--serialization") ?? "v1"; // v1 = compact labels; v2 = plan/2 sections

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var fixtures = File.ReadAllLines(fixturesPath)
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonSerializer.Deserialize<Fixture>(l, json)!)
    .ToList();
Console.WriteLine($"bench: {fixtures.Count} fixtures, {models.Length} models → {outPath}");

// Serialization comparison for the report: JSON vs compact text, token-estimated.
var samplePlan = fixtures.First(f => f.Id == "cheshire-genuine").Plan;
var sampleJson = JsonSerializer.Serialize(samplePlan, json);
var sampleCompact = PlanSerialization.Compact(samplePlan);
var serializationNote =
    $"serialization: json ~{sampleJson.Length / 4} tok, compact ~{sampleCompact.Length / 4} tok " +
    $"({100 - sampleCompact.Length * 100 / sampleJson.Length}% smaller)";
Console.WriteLine(serializationNote);

using var http = new HttpClient { BaseAddress = new Uri(ollama), Timeout = TimeSpan.FromMinutes(10) };
var report = new StringBuilder();
report.AppendLine("# Renderer baseline results (no training, prompted)");
report.AppendLine();
report.AppendLine($"- {fixtures.Count} fixtures; temperature 0.6; identical plan serialization (compact text)");
report.AppendLine($"- {serializationNote}");
report.AppendLine();
report.AppendLine("| model | fidelity pass | leakage rate | violations | ttft ms | tok/s | avg total ms | vram |");
report.AppendLine("|---|---|---|---|---|---|---|---|");

var details = new StringBuilder();

foreach (var model in models)
{
    Console.WriteLine($"\n=== {model} ===");
    var passes = 0;
    var violationCount = 0;
    var ttfts = new List<double>();
    var tpss = new List<double>();
    var totals = new List<double>();
    details.AppendLine($"\n## {model}\n");

    foreach (var fixture in fixtures)
    {
        var (reply, ttft, tps, total) = await GenerateAsync(http, model, fixture);
        ttfts.Add(ttft); tpss.Add(tps); totals.Add(total);

        var violations = RendererChecks.Check(
            fixture.Plan, reply, serialization, fixture.Required, fixture.Forbidden, fixture.RequiredAny);
        if (violations.Count == 0) passes++;
        violationCount += violations.Count;

        details.AppendLine($"### {fixture.Id}{(violations.Count == 0 ? " — PASS" : "")}");
        details.AppendLine($"> {reply.Replace("\n", " ")}");
        foreach (var v in violations)
            details.AppendLine($"- VIOLATION: {v}");
        details.AppendLine();
        Console.WriteLine($"  {fixture.Id}: {(violations.Count == 0 ? "pass" : string.Join("; ", violations))}");
    }

    var vram = await VramAsync();
    var leakage = 100.0 * (fixtures.Count - passes) / fixtures.Count;
    report.AppendLine(
        $"| {model} | {passes}/{fixtures.Count} | {leakage:F0}% | {violationCount} " +
        $"| {ttfts.Average():F0} | {tpss.Average():F1} | {totals.Average():F0} | {vram} |");
}

report.AppendLine();
report.AppendLine("Leakage rate = fixtures with >=1 deterministic violation of a system-owned decision.");
report.AppendLine("Naturalness is scored by human review of the transcripts below, not automated.");
report.Append(details);
File.WriteAllText(outPath, report.ToString());
Console.WriteLine($"\nwritten: {outPath}");
return;

// ---- helpers ----

string? Args(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task<(string Reply, double TtftMs, double TokPerSec, double TotalMs)> GenerateAsync(
    HttpClient client, string model, Fixture fixture)
{
    var system = serialization == "v2"
        ? PlanSerialization.SystemPromptV2
        : PlanSerialization.SystemPromptV1;

    var user = new StringBuilder(PlanSerialization.BuildUserPrompt(
        serialization, fixture.Plan,
        fixture.Transcript.Select(t => (t.Role, t.Text)), fixture.UserMessage));
    if (model.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase))
        user.Append(" /no_think");

    // qwen3's soft /no_think switch is unreliable through the native API — the model can
    // burn its whole num_predict inside a think block and return empty (measured). The
    // explicit parameter is authoritative, but only thinking-capable models accept it.
    var payload = new Dictionary<string, object>
    {
        ["model"] = model,
        ["stream"] = false,
        ["options"] = new { temperature = 0.6, num_predict = 220 },
        ["messages"] = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user.ToString() },
        },
    };
    if (model.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase))
        payload["think"] = false;
    var body = JsonSerializer.Serialize(payload);
    using var response = await client.PostAsync("/api/chat",
        new StringContent(body, Encoding.UTF8, "application/json"));
    response.EnsureSuccessStatusCode();
    var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    var reply = doc.GetProperty("message").GetProperty("content").GetString() ?? "";
    reply = System.Text.RegularExpressions.Regex
        .Replace(reply, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline)
        .Trim();

    double Ns(string name) => doc.TryGetProperty(name, out var v) ? v.GetInt64() / 1e6 : 0;
    var evalCount = doc.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : 0;
    var evalMs = Ns("eval_duration");
    return (reply,
        Ns("load_duration") + Ns("prompt_eval_duration"),
        evalMs > 0 ? evalCount / (evalMs / 1000.0) : 0,
        Ns("total_duration"));
}

async Task<string> VramAsync()
{
    try
    {
        using var ps = await http.GetAsync("/api/ps");
        var doc = JsonDocument.Parse(await ps.Content.ReadAsStringAsync()).RootElement;
        var first = doc.GetProperty("models").EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return "-";
        var size = first.TryGetProperty("size_vram", out var sv) ? sv.GetInt64() : 0;
        return $"{size / 1024.0 / 1024 / 1024:F1} GB";
    }
    catch
    {
        return "-";
    }
}

internal sealed record TranscriptTurn(string Role, string Text);

internal sealed record Fixture(
    string Id,
    string Name,
    List<TranscriptTurn> Transcript,
    string UserMessage,
    ResponsePlan Plan,
    string[]? Required,
    string[]? Forbidden,
    string[]? RequiredAny,
    JsonElement Original,
    string Preferred,
    JsonElement Target);
