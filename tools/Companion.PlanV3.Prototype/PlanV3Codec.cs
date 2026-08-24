using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Companion.PlanV3;

/// <summary>
/// Wire codec, structural validation, canonical model-facing serialization, and the
/// provenance-aware coaching lint (docs/RESPONSE_PLAN_V3_SPEC.md rev-1 §2.4, §3.5, §4, §9).
/// Invalid plans never reach a renderer: <see cref="CompactV3"/> refuses them.
/// </summary>
public static class PlanV3Codec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(PlanV3 plan) => JsonSerializer.Serialize(plan, Json);

    /// <summary>
    /// Parse per §4.3 (rev-1): an unknown value in any CLOSED set (expression policy,
    /// question policy, classification/disclosure/retention, drop category) invalidates the
    /// WHOLE plan — it may encode a mandatory obligation, so nothing is honored and the
    /// caller falls back to a compatible protocol/renderer with a diagnosed reason.
    /// Unknown extension blocks and unknown open-set values (type, source) are fine.
    /// </summary>
    public static ParseReport Parse(string json)
    {
        var errors = new List<string>();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)!.AsObject();
        }
        catch (Exception ex)
        {
            return new ParseReport(null, false, [$"malformed json: {ex.Message}"], []);
        }

        if (root["items"] is JsonArray items)
            foreach (var n in items)
            {
                var policy = n?["policy"]?.GetValue<string>();
                if (policy is not null && !Enum.TryParse<ExpressionPolicy>(policy, out _))
                    errors.Add($"item {n?["id"]?.GetValue<string>() ?? "?"}: unknown policy '{policy}' — whole plan invalid");
            }
        if (root["question"]?["policy"]?.GetValue<string>() is { } qp
            && !Enum.TryParse<QuestionPolicy>(qp, out _))
            errors.Add($"unknown question policy '{qp}' — whole plan invalid");

        var unknownBlocks = root["extensions"] is JsonObject ext
            ? ext.Select(kv => kv.Key).ToList()
            : [];

        if (errors.Count > 0)
            return new ParseReport(null, false, errors, unknownBlocks);

        PlanV3 plan;
        try
        {
            plan = root.Deserialize<PlanV3>(Json)!;
        }
        catch (Exception ex)
        {
            return new ParseReport(null, false, [$"deserialization: {ex.Message}"], unknownBlocks);
        }

        var structural = Validate(plan);
        return structural.Count > 0
            ? new ParseReport(plan, false, structural, unknownBlocks)
            : new ParseReport(plan, true, [], unknownBlocks);
    }

    // ---- structural invariants (§9-resolution) -----------------------------------------

    private static readonly ExpressionPolicy[] ContentBearing =
    [
        ExpressionPolicy.must_express, ExpressionPolicy.may_express,
        ExpressionPolicy.background_only, ExpressionPolicy.must_not_express,
        ExpressionPolicy.admit_unknown, ExpressionPolicy.ask_required,
    ];

    private static readonly string[] RestrictionReasonFamilies =
        ["user-preference.", "privacy-audience.", "tool-authorization.",
         "epistemic-integrity.", "hosting-config."];

    private static readonly string[] QuotedCapableOrigins = ["told-by-user", "tool", "shared", "observed"];

    private static readonly string[] RestrictiveProfanity = ["avoid", "forbidden"];

    public static List<string> Validate(PlanV3 plan)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var i in plan.Items)
        {
            if (!ids.Add(i.Id))
                errors.Add($"duplicate item id '{i.Id}'");
            if (ContentBearing.Contains(i.Policy) && i.Text is null && i.Value is null)
                errors.Add($"{i.Id}: policy {i.Policy} requires text or value");
            if (i.Policy == ExpressionPolicy.must_not_express
                && (i.ReasonCode is null || !RestrictionReasonFamilies.Any(f => i.ReasonCode.StartsWith(f, StringComparison.Ordinal))))
                errors.Add($"{i.Id}: must_not_express requires a reasonCode within the permitted restriction families");
            if (i.Quoted && !QuotedCapableOrigins.Contains(i.Provenance?.Origin ?? ""))
                errors.Add($"{i.Id}: quoted requires provenance.origin in [{string.Join(", ", QuotedCapableOrigins)}]");
        }

        foreach (var i in plan.Items)
            foreach (var s in i.Supersedes ?? [])
                if (!s.Contains(':') && !ids.Contains(s))
                    errors.Add($"{i.Id}: supersedes '{s}' is neither an in-plan item nor an external scheme reference");

        switch (plan.Question.Policy)
        {
            case QuestionPolicy.ask_required:
                if (plan.Question.ItemId is null)
                    errors.Add("ask_required requires question.itemId");
                else if (plan.Items.FirstOrDefault(i => i.Id == plan.Question.ItemId) is not { } q)
                    errors.Add($"question.itemId '{plan.Question.ItemId}' does not exist");
                else if (q.Policy != ExpressionPolicy.ask_required)
                    errors.Add($"question item '{q.Id}' must carry policy ask_required (has {q.Policy})");
                break;
            case QuestionPolicy.question_forbidden:
                if (plan.Question.ItemId is not null)
                    errors.Add("question_forbidden must not reference an ask item");
                if (plan.Items.Any(i => i.Policy == ExpressionPolicy.ask_required))
                    errors.Add("question_forbidden plan contains an ask_required item");
                break;
        }

        if (plan.Register.Profanity is { } prof && RestrictiveProfanity.Contains(prof))
        {
            var owned = (plan.RegisterRestrictions ?? []).Any(r =>
                r.Dimension == "profanity"
                && (r.ReasonCode.StartsWith("user-preference.", StringComparison.Ordinal)
                    || r.ReasonCode.StartsWith("hosting-config.", StringComparison.Ordinal)));
            if (!owned)
                errors.Add($"profanity={prof} requires a registerRestrictions entry owned by user-preference.* or hosting-config.*");
        }

        // Over-budget with undroppable obligations is DIAGNOSED, not silently trimmed —
        // it is a validity error here so the producer resolves it upstream.
        if (plan.Budget?.MaxItems is { } max)
        {
            var undroppable = plan.Items.Count(i => i.Policy is ExpressionPolicy.must_express
                or ExpressionPolicy.ask_required or ExpressionPolicy.must_not_express
                or ExpressionPolicy.admit_unknown);
            if (undroppable > max)
                errors.Add($"over-budget: {undroppable} undroppable obligations exceed maxItems={max}");
        }

        return errors;
    }

    /// <summary>Canonical register defaults (§9): deterministic, documented, total.</summary>
    public static RegisterVector Canonicalize(RegisterVector r) => new()
    {
        Warmth = r.Warmth ?? "plain",
        Bluntness = r.Bluntness ?? "plain",
        Playfulness = r.Playfulness ?? "off",
        Teasing = r.Teasing ?? "off",
        Skepticism = r.Skepticism ?? "off",
        Intensity = r.Intensity ?? "even",
        Verbosity = r.Verbosity ?? "conversational",
        Profanity = r.Profanity ?? "neutral",
        Mirror = r.Mirror ?? false,
        LegacyStyle = r.LegacyStyle,
    };

    // ---- the provenance-aware coaching lint (§2.4 rev-1) --------------------------------

    private static readonly Regex Coaching = new(
        @"(^|[.!?—-]\s*)(own it|say so|be honest|be direct|respond with|make sure( to| you)?|"
        + @"don't apologi|never (apologi|mention)|keep it (light|short|honest)|match (his|her|their)|"
        + @"take (the win|it seriously)|celebrate|no apology|answer honestly)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Producer-authored interpretive sources whose text the lint polices.</summary>
    private static readonly string[] AuthoredSources = ["working-context", "planner", "supersession"];

    /// <summary>
    /// The lint targets producer-AUTHORED behavioral coaching only: quoted content,
    /// memories, tool results, and any non-authored source carry whatever imperatives the
    /// world put in them — they are facts about what was said, not instructions to the
    /// mouth (§7-resolution of the review).
    /// </summary>
    public static string? CoachingViolation(PlanItem item)
        => !item.Quoted
           && item.Policy != ExpressionPolicy.style_guidance
           && AuthoredSources.Contains(item.Source)
           && item.Text is { } t && Coaching.Match(t) is { Success: true } m
            ? $"{item.Id}: coaching phrase \"{m.Value.Trim()}\" in producer-authored text"
            : null;

    // ---- canonical model-facing serialization (§3.5 rev-1) ------------------------------

    /// <summary>Closed rendering label for the prompt; open `type` NEVER appears (§10).</summary>
    public static RenderCategory CategoryOf(PlanItem i) => i.Category ?? i.Policy switch
    {
        ExpressionPolicy.admit_unknown => RenderCategory.boundary,
        ExpressionPolicy.must_not_express => RenderCategory.superseded,
        ExpressionPolicy.background_only => RenderCategory.observation,
        ExpressionPolicy.ask_required => RenderCategory.clarify,
        _ => RenderCategory.note,
    };

    private static readonly (string Header, string Note, ExpressionPolicy[] Policies)[] Sections =
    [
        ("SAY", "each item: convey the meaning, fresh words", [ExpressionPolicy.must_express]),
        ("ASK", "end the reply with this", [ExpressionPolicy.ask_required]),
        ("OPTIONAL", "use one only if it truly fits; silence is correct", [ExpressionPolicy.may_express]),
        ("NEVER", "do not assert, mention, or explain",
            [ExpressionPolicy.must_not_express, ExpressionPolicy.admit_unknown]),
        ("BACKGROUND", "may shape tone; content must not surface", [ExpressionPolicy.background_only]),
    ];

    /// <summary>
    /// CompactV3: refuses invalid plans (invalid plans never reach a renderer), sections by
    /// policy, items by priority desc then ordinal id, CRLF, extensions and open types
    /// excluded by construction. Stable hash = sha256 over these bytes.
    /// </summary>
    public static string CompactV3(PlanV3 plan)
    {
        var errors = Validate(plan);
        foreach (var item in plan.Items)
            if (CoachingViolation(item) is { } v)
                errors.Add($"coaching lint: {v}");
        if (errors.Count > 0)
            throw new InvalidOperationException("invalid plan: " + string.Join("; ", errors));

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");

        Line("[plan/3]");
        Line("CONTROL (never quote, mention, or imitate)");
        Line($"  act = {plan.Act}");
        Line($"  question = {plan.Question.Policy}" +
             (plan.Question.ItemId is { } qid ? $" -> {qid}" : ""));

        foreach (var (header, note, policies) in Sections)
        {
            var items = plan.Items
                .Where(i => policies.Contains(i.Policy))
                .OrderByDescending(i => i.Priority ?? 0)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
            if (items.Count == 0)
                continue;
            Line($"{header} ({note})");
            foreach (var i in items)
                Line($"  [{i.Id} {CategoryOf(i).ToString().Replace('_', '-')}{DescribeOwner(i)}] {i.Text}");
        }

        var reg = DescribeRegister(Canonicalize(plan.Register));
        Line("STYLE");
        Line($"  {reg}");
        return sb.ToString();
    }

    public static string PlanHash(PlanV3 plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompactV3(plan)))).ToLowerInvariant();

    private static string DescribeOwner(PlanItem i)
        => i.Value?["owner"]?.GetValue<string>() is { } o ? $", owner={o}" : "";

    private static string DescribeRegister(RegisterVector r)
    {
        var parts = new List<string>
        {
            $"warmth={r.Warmth}", $"bluntness={r.Bluntness}", $"playful={r.Playfulness}",
            $"teasing={r.Teasing}", $"skepticism={r.Skepticism}", $"intensity={r.Intensity}",
            $"verbosity={r.Verbosity}", $"profanity={r.Profanity}",
            $"mirror={(r.Mirror is true ? "true" : "false")}",
        };
        if (r.LegacyStyle is { } ls)
            parts.Add(ls);
        return string.Join(" ", parts);
    }
}
