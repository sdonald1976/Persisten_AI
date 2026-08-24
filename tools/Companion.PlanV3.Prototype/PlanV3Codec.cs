using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Companion.PlanV3;

/// <summary>
/// Wire codec + canonical model-facing serialization + the coaching lint
/// (docs/RESPONSE_PLAN_V3_SPEC.md §2.4, §3.5, §4.3).
/// </summary>
public static class PlanV3Codec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(PlanV3 plan) => JsonSerializer.Serialize(plan, Json);

    /// <summary>
    /// Lenient parse per §4.3: unknown extension blocks survive verbatim; an item with an
    /// unknown POLICY is rejected (fail closed — an obligation is never guessed) and named
    /// in the report; unknown item type/source are valid open-set values and pass through.
    /// </summary>
    public static ParseReport Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var rejected = new List<string>();

        if (root["items"] is JsonArray items)
        {
            var keep = new JsonArray();
            foreach (var n in items.ToList())
            {
                var policy = n?["policy"]?.GetValue<string>();
                if (policy is not null && !Enum.TryParse<ExpressionPolicy>(policy, out _))
                {
                    rejected.Add($"{n?["id"]?.GetValue<string>() ?? "?"}: unknown policy '{policy}'");
                    continue;
                }
                keep.Add(n!.DeepClone());
            }
            root["items"] = keep;
        }

        var unknownBlocks = root["extensions"] is JsonObject ext
            ? ext.Select(kv => kv.Key).ToList()
            : [];

        var plan = root.Deserialize<PlanV3>(Json)!;
        return new ParseReport(plan, rejected, unknownBlocks);
    }

    // ---- the coaching lint (§2.4): imperative second-person instruction is not a fact ----

    private static readonly Regex Coaching = new(
        @"(^|[.!?—-]\s*)(own it|say so|be honest|be direct|respond with|make sure( to| you)?|"
        + @"don't apologi|never (apologi|mention)|keep it (light|short|honest)|match (his|her|their)|"
        + @"take (the win|it seriously)|celebrate|no apology|answer honestly)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? CoachingViolation(PlanItem item)
        => item.Policy != ExpressionPolicy.style_guidance
           && item.Text is { } t && Coaching.Match(t) is { Success: true } m
            ? $"{item.Id}: coaching phrase \"{m.Value.Trim()}\" in item text"
            : null;

    // ---- canonical model-facing serialization (§3.5) ----

    private static readonly (string Header, string Note, Func<PlanV3, IEnumerable<PlanItem>> Pick)[] Sections =
    [
        ("SAY", "each item: convey the meaning, fresh words",
            p => p.Items.Where(i => i.Policy is ExpressionPolicy.must_express)),
        ("ASK", "end the reply with this",
            p => p.Items.Where(i => i.Policy is ExpressionPolicy.ask_required)),
        ("OPTIONAL", "use one only if it truly fits; silence is correct",
            p => p.Items.Where(i => i.Policy is ExpressionPolicy.may_express)),
        ("NEVER", "do not assert, mention, or explain",
            p => p.Items.Where(i => i.Policy is ExpressionPolicy.must_not_express or ExpressionPolicy.admit_unknown)),
        ("BACKGROUND", "may shape tone; content must not surface",
            p => p.Items.Where(i => i.Policy is ExpressionPolicy.background_only)),
    ];

    /// <summary>
    /// CompactV3: deterministic, sectioned by policy, items ordered by priority desc then id
    /// (ordinal), CRLF line endings, extensions NEVER serialized. The stable plan hash is
    /// sha256 over these bytes.
    /// </summary>
    public static string CompactV3(PlanV3 plan)
    {
        foreach (var item in plan.Items)
            if (CoachingViolation(item) is { } v)
                throw new InvalidOperationException($"coaching lint: {v}");

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");

        Line("[plan/3]");
        Line("CONTROL (never quote, mention, or imitate)");
        Line($"  act = {plan.Act}");
        Line($"  question = {plan.Question.Policy}" +
             (plan.Question.ItemId is { } qid ? $" -> {qid}" : ""));

        foreach (var (header, note, pick) in Sections)
        {
            var items = pick(plan)
                .OrderByDescending(i => i.Priority ?? 0)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
            if (items.Count == 0)
                continue;
            Line($"{header} ({note})");
            foreach (var i in items)
                Line($"  [{i.Id} {i.Type}{DescribeOwner(i)}] {i.Text}");
        }

        var reg = DescribeRegister(plan.Register);
        if (reg.Length > 0)
        {
            Line("STYLE");
            Line($"  {reg}");
        }
        return sb.ToString();
    }

    public static string PlanHash(PlanV3 plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompactV3(plan)))).ToLowerInvariant();

    private static string DescribeOwner(PlanItem i)
        => i.Value?["owner"]?.GetValue<string>() is { } o ? $", owner={o}" : "";

    private static string DescribeRegister(RegisterVector r)
    {
        var parts = new List<string>();
        void Add(string k, string? v) { if (v is not null) parts.Add($"{k}={v}"); }
        Add("warmth", r.Warmth); Add("bluntness", r.Bluntness); Add("playful", r.Playfulness);
        Add("teasing", r.Teasing); Add("skepticism", r.Skepticism); Add("intensity", r.Intensity);
        Add("verbosity", r.Verbosity); Add("profanity", r.Profanity);
        if (r.Mirror is { } m) parts.Add($"mirror={(m ? "true" : "false")}");
        if (r.LegacyStyle is { } ls) parts.Add(ls);
        return string.Join(" ", parts);
    }
}
