using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Companion.Core.Domain;
using Companion.RendererBench;

// QLoRA run-1 dataset generator (training/renderer/QLORA_DESIGN.md). Reads authored
// scenario files, serializes each plan with the SAME plan/2 code the bench uses, asks
// the teacher models for candidate targets, and runs the full deterministic gate suite
// plus sludge flags over every candidate. Teachers propose; gates decide ELIGIBILITY;
// a curator decides gold. Every attempt — accepted or rejected — is preserved with
// full lineage so a future verbal tic can be traced to its source.
//
// Offline, read-only against the repo; talks only to Ollama; writes only to --out.

var scenariosDir = Args("--scenarios") ?? @"training\renderer\dataset\scenarios";
var ollama = Args("--ollama") ?? "http://localhost:11434";
var outPath = Args("--out") ?? @"training\renderer\dataset\candidates.jsonl";
var teachers = (Args("--teachers") ?? "qwen3:8b,llama3.2:3b").Split(',');
var maxAttempts = int.TryParse(Args("--attempts"), out var a) ? a : 2;
var only = Args("--only");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var scenarios = Directory.GetFiles(scenariosDir, "*.jsonl")
    .SelectMany(File.ReadAllLines)
    .Where(l => !string.IsNullOrWhiteSpace(l))
    .Select(l => JsonSerializer.Deserialize<Scenario>(l, json)!)
    .Where(s => only is null || s.Id.StartsWith(only, StringComparison.OrdinalIgnoreCase))
    .ToList();

var duplicateIds = scenarios.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
if (duplicateIds.Count > 0)
    throw new InvalidOperationException($"duplicate scenario ids: {string.Join(", ", duplicateIds)}");

// Resume: (scenario, teacher) pairs already generated are never re-spent.
var done = File.Exists(outPath)
    ? File.ReadAllLines(outPath).Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => JsonNode.Parse(l)!)
        .Select(n => $"{n["id"]!.GetValue<string>()}|{n["teacher"]!.GetValue<string>()}")
        .ToHashSet()
    : [];
Console.WriteLine($"datasetgen: {scenarios.Count} scenarios x {teachers.Length} teachers " +
                  $"({done.Count} pairs already done)");

using var http = new HttpClient { BaseAddress = new Uri(ollama), Timeout = TimeSpan.FromMinutes(10) };
using var outStream = new StreamWriter(outPath, append: true, Encoding.UTF8);
var generated = 0;

// Teacher OUTERMOST: a 6 GB card cannot hold an 8B and a 3B at once, so alternating
// per scenario would pay a model load on every single call.
foreach (var teacher in teachers)
{
    Console.WriteLine($"\n=== teacher: {teacher} ===");
    foreach (var s in scenarios)
    {
        if (done.Contains($"{s.Id}|{teacher}")) continue;
        var attempts = new List<Candidate>();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var text = await GenerateAsync(http, teacher, s);
            var violations = RendererChecks.Check(s.Plan, text, "v2", s.Required, s.Forbidden, s.RequiredAny);
            var sludge = RendererChecks.SludgeFlags(text);
            attempts.Add(new Candidate(
                teacher, attempt, text, violations, sludge,
                RendererChecks.WordCount(text), RendererChecks.Vocatives(text),
                RendererChecks.OpeningNgram(text), violations.Count == 0));
            if (violations.Count == 0) break; // eligible on this attempt — stop retrying
        }
        var row = new CandidateRow(
            s.Id, s.Family, s.Stratum, s.Source, PlanSerialization.CompactV2(s.Plan),
            s.Transcript, s.UserMessage, s.Required, s.Forbidden, s.RequiredAny,
            teacher, attempts);
        outStream.WriteLine(JsonSerializer.Serialize(row, json));
        outStream.Flush();
        generated++;
        Console.WriteLine($"  [{generated}] {s.Id}: {attempts.Count} attempt(s), " +
                          $"{(attempts.Any(c => c.Eligible) ? "eligible" : "REJECTED")}");
    }
}
Console.WriteLine($"done: {generated} new rows -> {outPath}");
return;

string? Args(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task<string> GenerateAsync(HttpClient client, string model, Scenario s)
{
    var user = PlanSerialization.BuildUserPrompt(
        "v2", s.Plan, s.Transcript.Select(t => (t.Role, t.Text)), s.UserMessage);
    var payload = new Dictionary<string, object>
    {
        ["model"] = model,
        ["stream"] = false,
        ["options"] = new { temperature = 0.6, num_predict = 220 },
        ["messages"] = new object[]
        {
            new { role = "system", content = PlanSerialization.SystemPromptV2 },
            new { role = "user", content = user },
        },
    };
    if (model.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase))
        payload["think"] = false;
    using var response = await client.PostAsync("/api/chat",
        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    response.EnsureSuccessStatusCode();
    var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    var reply = doc.GetProperty("message").GetProperty("content").GetString() ?? "";
    return System.Text.RegularExpressions.Regex
        .Replace(reply, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline)
        .Trim();
}

internal sealed record TranscriptTurn(string Role, string Text);

internal sealed record Scenario(
    string Id,
    string Family,
    string Stratum,
    JsonElement Source,
    List<TranscriptTurn> Transcript,
    string UserMessage,
    ResponsePlan Plan,
    string[]? Required,
    string[]? Forbidden,
    string[]? RequiredAny);

internal sealed record Candidate(
    string Teacher,
    int Attempt,
    string Text,
    List<string> Violations,
    List<string> Sludge,
    int Words,
    int Vocatives,
    string Opening,
    bool Eligible);

internal sealed record CandidateRow(
    string Id,
    string Family,
    string Stratum,
    JsonElement Source,
    string Plan2,
    List<TranscriptTurn> Transcript,
    string UserMessage,
    string[]? Required,
    string[]? Forbidden,
    string[]? RequiredAny,
    string Teacher,
    List<Candidate> Candidates);
