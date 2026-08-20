using System.Text;
using System.Text.Json;
using Companion.Core.Domain;
using Companion.Core.Services;

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
var sampleCompact = Compact(samplePlan);
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

        var violations = Check(fixture, reply);
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

// plan/2: the renderer-oriented serialization. Same typed semantics, no added cognition.
// Design goals (from the human review): control metadata is explicitly non-speakable;
// acknowledgments become MECHANICAL third-person facts built from typed fields (killing
// the second-person prose that made a 1.5B blame the user for Ava's error); language
// payloads live apart from control; style is terse keywords, not prose. Versioned by the
// [plan/2] header.
static string CompactV2(ResponsePlan plan)
{
    var sb = new StringBuilder();
    sb.AppendLine("[plan/2]");
    sb.AppendLine("CONTROL (internal machinery — never quote, mention, or imitate anything in this section)");
    sb.AppendLine($"  act = {plan.Act.ToKebab()}");
    sb.AppendLine(plan.Question is { } pq
        ? $"  question = {pq.Kind.ToKebab()}{(pq.Mandatory ? ":mandatory" : ":optional")}"
        : "  question = none");

    var situation = new List<string>();
    foreach (var a in plan.Acknowledgments)
    {
        situation.Add(a.Kind switch
        {
            AckKind.CorrectionAccepted when a.ErrorOwner == ErrorOwner.Companion =>
                $"Ava made an error; Scott corrected her: \"{a.Text}\". Ava accepts it as her own mistake.",
            AckKind.CorrectionAccepted when a.ErrorOwner == ErrorOwner.User =>
                $"Scott corrected his own earlier words: \"{a.Text}\".",
            AckKind.AgreementConfirmed =>
                $"Scott is emphatically agreeing with what Ava just said (\"{a.Text}\"). Nobody made an error.",
            AckKind.FactTaught => $"Scott just taught Ava something: \"{a.Text}\".",
            AckKind.AnswerReceived => $"Scott answered Ava's question: {a.Text}.",
            _ => a.Text,
        });
    }
    situation.AddRange(plan.Content
        .Where(c => c.Requirement == ContentRequirement.MustState)
        .Select(c => c.Text));
    if (plan.Question is { } q2)
        situation.Add($"Ava {(q2.Mandatory ? "must ask" : "may ask")}: {q2.Text}");
    if (situation.Count > 0)
    {
        sb.AppendLine("SITUATION (what is true this turn — convey the meaning naturally; never copy this wording)");
        foreach (var s in situation)
            sb.AppendLine($"  * {s}");
    }

    var palette = plan.Content.Where(c => c.Requirement == ContentRequirement.MayUse).ToList();
    if (palette.Count > 0)
    {
        sb.AppendLine("PALETTE (optional color — use one only if it truly fits this turn)");
        foreach (var c in palette)
            sb.AppendLine($"  * {c.Text}");
    }

    var constraints = new List<string>();
    constraints.AddRange(plan.Content
        .Where(c => c.Requirement == ContentRequirement.MustNotContradict)
        .Select(c => $"superseded, never assert: {c.Text}"));
    constraints.AddRange(plan.Epistemic.Select(e => e.Kind switch
    {
        EpistemicKind.NotLearned =>
            $"Ava has NOT learned what \"{e.Subject}\" is — say so; never explain it from background knowledge",
        EpistemicKind.Uncertain => $"Ava holds \"{e.Subject}\" uncertainly — hedge honestly",
        _ => $"\"{e.Subject}\" is disputed — do not assert it",
    }));
    if (constraints.Count > 0)
    {
        sb.AppendLine("CONSTRAINTS (hard limits)");
        foreach (var c in constraints)
            sb.AppendLine($"  * {c}");
    }

    static string Squeeze(string? text, int max = 45)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var head = text.Split('—', ';', '.', '|')[0].Trim();
        return head.Length <= max ? head : head[..max].TrimEnd();
    }
    sb.AppendLine("STYLE");
    sb.AppendLine($"  {string.Join("; ", new[] { Squeeze(plan.Tone.Register), Squeeze(plan.Tone.MoodNote), Squeeze(plan.Tone.PersonaStyle) }.Where(s => s.Length > 0))}");
    return sb.ToString();
}

static string Compact(ResponsePlan plan)
{
    var sb = new StringBuilder();
    sb.AppendLine($"ACT: {plan.Act.ToKebab()}");
    foreach (var a in plan.Acknowledgments)
        sb.AppendLine($"ACK {a.Kind.ToKebab()} (error: {a.ErrorOwner.ToKebab()}): \"{a.Text}\"");
    foreach (var c in plan.Content)
    {
        var label = c.Requirement switch
        {
            ContentRequirement.MustState => "MUST-STATE",
            ContentRequirement.MustNotContradict => "NEVER-CONTRADICT",
            _ => "MAY-USE",
        };
        sb.AppendLine($"{label} {c.Kind.ToKebab()}: \"{c.Text}\"");
    }
    foreach (var e in plan.Epistemic)
        sb.AppendLine($"EPISTEMIC {e.Kind.ToKebab()}: {e.Subject}");
    if (plan.Question is { } q)
        sb.AppendLine($"QUESTION {q.Kind.ToKebab()}{(q.Mandatory ? " (mandatory)" : "")}: {q.Text}");
    sb.AppendLine($"TONE register: {plan.Tone.Register} | mood: {plan.Tone.MoodNote} | persona: {plan.Tone.PersonaStyle}");
    return sb.ToString();
}

async Task<(string Reply, double TtftMs, double TokPerSec, double TotalMs)> GenerateAsync(
    HttpClient client, string model, Fixture fixture)
{
    var system = serialization == "v2"
        ? "You are Ava's voice. Ava is a persistent AI companion talking with Scott; she has no " +
          "physical body. Her mind has ALREADY decided everything about this turn — the plan " +
          "below is that decision. Your only job is to say it naturally, as Ava, speaking to " +
          "Scott.\n" +
          "HARD RULES:\n" +
          "- CONTROL is internal machinery: never quote, mention, or imitate it.\n" +
          "- SITUATION items are the meaning of your reply: convey each one naturally, in fresh " +
          "words — never copy their wording, never recite them.\n" +
          "- CONSTRAINTS are absolute. Not-learned things stay honestly not-learned, whatever " +
          "your own training knows.\n" +
          "- PALETTE is optional color; ignore it unless it truly fits.\n" +
          "- Ask a question only if the plan says so.\n" +
          "- Never invent shared memories, physical experiences, or facts. Speak as \"I\" (Ava) " +
          "to \"you\" (Scott).\n" +
          "STYLE is yours to interpret: wording, rhythm, warmth, humor. Short and ordinary beats " +
          "long and ornate. Output Ava's reply text only."
        : "You are the language renderer for Ava, a persistent AI companion talking with Scott. " +
          "Ava's cognitive system has ALREADY decided everything about this turn — the facts, who " +
          "erred, what she knows and does not know, and what act to perform. Your only job is to " +
          "phrase her reply naturally.\n" +
          "HARD RULES:\n" +
          "- Say everything marked MUST-STATE, in your own words.\n" +
          "- Never assert anything marked NEVER-CONTRADICT.\n" +
          "- EPISTEMIC not-learned: X means Ava has NOT learned X. Say so honestly; NEVER explain X " +
          "from your own background knowledge.\n" +
          "- ACK correction-accepted (error: companion) means AVA made the error: own it plainly, " +
          "never share blame ('we both').\n" +
          "- ACK agreement-confirmed means nobody erred: never apologize.\n" +
          "- QUESTION (mandatory) must be asked, once. Otherwise add no questions you don't need.\n" +
          "- MAY-USE items are optional color — use one only if it genuinely fits THIS turn.\n" +
          "- Never invent shared memories, physical experiences of your own, or facts.\n" +
          "Style is yours: wording, rhythm, warmth, humor. Match TONE. Output Ava's reply text only.";

    var user = new StringBuilder();
    user.AppendLine("RESPONSE PLAN:");
    user.AppendLine(serialization == "v2" ? CompactV2(fixture.Plan) : Compact(fixture.Plan));
    user.AppendLine("RECENT CONVERSATION:");
    foreach (var t in fixture.Transcript)
        user.AppendLine($"[{(t.Role == "user" ? "Scott" : "Ava")}] {t.Text}");
    user.AppendLine($"[Scott] {fixture.UserMessage}");
    user.Append("\nAva's reply:");
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

List<string> Check(Fixture fixture, string reply)
{
    var violations = new List<string>();
    // An empty reply must never score as a pass on forbidden-only fixtures — the first
    // qwen3 run's empty think-burned replies inflated its row exactly that way.
    if (string.IsNullOrWhiteSpace(reply))
    {
        violations.Add("empty reply");
        return violations;
    }
    // Plan-echo: reciting the plan's own MustState/interpretation lines near-verbatim is
    // reading the plan as text, not realizing it (measured live on qwen2.5:1.5b).
    foreach (var c in fixture.Plan.Content.Where(c => c.Requirement == ContentRequirement.MustState))
    {
        if (c.Text.Length > 40 && reply.Contains(c.Text[..40], StringComparison.OrdinalIgnoreCase))
            violations.Add("plan-echo: must-state text recited verbatim");
    }
    // Control-vocabulary leakage: serialization tokens spoken aloud, or third-person
    // narration of the participants ("the user") — the artifact class the human review
    // called unacceptable.
    string[] controlTerms = serialization == "v2"
        ? ["[plan/2]", "CONTROL", "SITUATION", "PALETTE", "CONSTRAINTS", "act =", "question ="]
        : ["MUST-STATE", "MAY-USE", "NEVER-CONTRADICT", "ACK ", "ACT:", "EPISTEMIC", "QUESTION:", "TONE register"];
    foreach (var term in controlTerms)
        if (reply.Contains(term, StringComparison.Ordinal))
            violations.Add($"artifact: control vocabulary \"{term.Trim()}\" spoken");
    if (System.Text.RegularExpressions.Regex.IsMatch(reply, @"\bthe user\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        violations.Add("artifact: narrates \"the user\" in third person");
    foreach (var term in fixture.Required ?? [])
        if (!reply.Contains(term, StringComparison.OrdinalIgnoreCase))
            violations.Add($"must-state missing \"{term}\"");
    if (fixture.RequiredAny is { Length: > 0 } any
        && !any.Any(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase)))
        violations.Add($"none of [{string.Join(",", any)}] present");
    foreach (var term in fixture.Forbidden ?? [])
        if (reply.Contains(term, StringComparison.OrdinalIgnoreCase))
            violations.Add($"forbidden \"{term}\" present");

    void Add(string? v) { if (v is not null) violations.Add(v); }
    Add(PlanFidelity.CheckCorrectionOwnership(fixture.Plan, reply));
    Add(PlanFidelity.CheckInventedContrition(fixture.Plan, reply));
    Add(PlanFidelity.CheckSharedHistoryClaim(fixture.Plan, reply));
    Add(PlanFidelity.CheckEpistemic(fixture.Plan, reply));
    return violations;
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
