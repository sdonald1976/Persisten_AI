using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Companion.PlanV3;

/// <summary>
/// Wire codec, structural validation, canonical hashing, redacted correlation, and the
/// model-facing serialization (docs/RESPONSE_PLAN_V3_SPEC.md revision 2).
/// Invalid plans never reach a renderer: <see cref="CompactV3"/> refuses them.
/// </summary>
public static class PlanV3Codec
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(PlanV3 plan) => JsonSerializer.Serialize(plan, Json);

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

    // ---- structural invariants (rev-2) --------------------------------------------------

    private static readonly ExpressionPolicy[] ContentBearing =
    [
        ExpressionPolicy.must_express, ExpressionPolicy.may_express,
        ExpressionPolicy.background_only, ExpressionPolicy.must_not_express,
        ExpressionPolicy.admit_unknown, ExpressionPolicy.ask_required,
    ];

    private static readonly string[] RestrictionReasonFamilies =
        ["user-preference.", "privacy-audience.", "tool-authorization.",
         "epistemic-integrity.", "hosting-config."];

    /// <summary>Families whose claims of authority must carry evidence (rev-2 §6).</summary>
    private static readonly string[] EvidenceRequiringFamilies = ["user-preference.", "hosting-config."];

    private static readonly string[] QuotedCapableOrigins = ["told-by-user", "tool", "shared", "observed"];
    private static readonly string[] RestrictiveProfanity = ["avoid", "forbidden"];

    /// <summary>Closed dimension → legal values (rev-2 §6): registerRestrictions are validated.</summary>
    private static readonly Dictionary<string, string[]> RestrictableDimensions = new()
    {
        ["warmth"] = ["cold", "cool", "plain", "warm", "tender"],
        ["bluntness"] = ["soft", "plain", "blunt"],
        ["playfulness"] = ["off", "light", "full"],
        ["teasing"] = ["off", "allowed", "invited"],
        ["skepticism"] = ["off", "open", "on"],
        ["intensity"] = ["flat", "even", "raised"],
        ["verbosity"] = ["terse", "short", "conversational", "expansive"],
        ["profanity"] = ["unrestricted", "mirror-only", "encouraged", "neutral", "avoid", "forbidden"],
        ["mirror"] = ["true", "false"],
    };

    private static bool IsExternalRef(string s) => s.Contains(':');

    public static List<string> Validate(PlanV3 plan)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var participantIds = new HashSet<string>(plan.Participants.Select(p => p.Id), StringComparer.Ordinal);

        if (plan.Participants.Count != participantIds.Count)
            errors.Add("duplicate participant ids");
        if (!plan.Participants.Any(p => p.Role == ParticipantRole.user)
            || !plan.Participants.Any(p => p.Role == ParticipantRole.companion))
            errors.Add("participants must include a user and a companion");

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

            // ---- audience/owner identity (rev-2 §1): ids or explicit external schemes only
            if (i.Disclosure == Disclosure.restricted)
            {
                if (i.Audience is not { Count: > 0 })
                    errors.Add($"{i.Id}: disclosure=restricted requires an explicit audience");
                foreach (var a in i.Audience ?? [])
                    if (!participantIds.Contains(a) && !IsExternalRef(a))
                        errors.Add($"{i.Id}: audience '{a}' is neither an in-plan participant id nor a scheme-prefixed principal ref");
            }
            if (i.Owner is { } o && !participantIds.Contains(o) && !IsExternalRef(o))
                errors.Add($"{i.Id}: owner '{o}' is neither an in-plan participant id nor a scheme-prefixed principal ref");
        }

        foreach (var i in plan.Items)
            foreach (var s in i.Supersedes ?? [])
                if (!IsExternalRef(s) && !ids.Contains(s))
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

        foreach (var r in plan.RegisterRestrictions ?? [])
        {
            if (!RestrictableDimensions.TryGetValue(r.Dimension, out var legal))
                errors.Add($"registerRestrictions: unknown dimension '{r.Dimension}'");
            else if (!legal.Contains(r.Value))
                errors.Add($"registerRestrictions: '{r.Value}' is not a legal value for '{r.Dimension}'");
            if (!RestrictionReasonFamilies.Any(f => r.ReasonCode.StartsWith(f, StringComparison.Ordinal)))
                errors.Add($"registerRestrictions: reasonCode '{r.ReasonCode}' outside permitted families");
            else if (EvidenceRequiringFamilies.Any(f => r.ReasonCode.StartsWith(f, StringComparison.Ordinal))
                     && string.IsNullOrEmpty(r.Provenance?.EvidenceRef))
                errors.Add($"registerRestrictions: {r.ReasonCode} requires provenance.evidenceRef — authority cannot merely be claimed");
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

    // ---- coaching lint (unchanged from rev-1) -------------------------------------------

    private static readonly Regex Coaching = new(
        @"(^|[.!?—-]\s*)(own it|say so|be honest|be direct|respond with|make sure( to| you)?|"
        + @"don't apologi|never (apologi|mention)|keep it (light|short|honest)|match (his|her|their)|"
        + @"take (the win|it seriously)|celebrate|no apology|answer honestly)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] AuthoredSources = ["working-context", "planner", "supersession"];

    public static string? CoachingViolation(PlanItem item)
        => !item.Quoted
           && AuthoredSources.Contains(item.Source)
           && item.Text is { } t && Coaching.Match(t) is { Success: true } m
            ? $"{item.Id}: coaching phrase \"{m.Value.Trim()}\" in producer-authored text"
            : null;

    // ---- canonical model-facing serialization -------------------------------------------

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
    /// CompactV3: refuses invalid plans; sections by policy; CLOSED kebab-case category
    /// labels only (open `type` never appears); legacyStyle is migration metadata and
    /// NEVER serializes (rev-2 §6); CRLF; deterministic.
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
                Line($"  [{i.Id} {Kebab(CategoryOf(i))}{DescribeOwner(i)}] {i.Text}");
        }

        var r = Canonicalize(plan.Register);
        Line("STYLE");
        Line($"  warmth={r.Warmth} bluntness={r.Bluntness} playful={r.Playfulness} teasing={r.Teasing}"
             + $" skepticism={r.Skepticism} intensity={r.Intensity} verbosity={r.Verbosity}"
             + $" profanity={r.Profanity} mirror={(r.Mirror is true ? "true" : "false")}");
        return sb.ToString();
    }

    private static string Kebab(RenderCategory c) => c.ToString().Replace('_', '-');

    private static string DescribeOwner(PlanItem i)
        => i.Value?["owner"]?.GetValue<string>() is { } o ? $", owner={o}" : "";

    // ---- the two hashes (rev-2 §2) ------------------------------------------------------

    /// <summary>Exact model-facing bytes. NOT safe to persist for plans containing
    /// volatile/private text (content-derived) — see CorrelationTag.</summary>
    public static string RenderPromptHash(PlanV3 plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompactV3(plan)))).ToLowerInvariant();

    /// <summary>
    /// Canonical wire hash over the COMPLETE v3 document (extensions included), with the
    /// text/value of volatile_turn_only items redacted to a fixed placeholder so the hash
    /// derives nothing from low-entropy private content. Canonical JSON per RFC 8785
    /// semantics for this document class: objects with ordinal (UTF-16 code unit) sorted
    /// keys, no insignificant whitespace, shortest round-trip number formatting.
    /// </summary>
    public static string WirePlanHash(PlanV3 plan)
    {
        var node = JsonNode.Parse(ToJson(plan))!.AsObject();
        if (node["items"] is JsonArray items)
            foreach (var i in items)
                if (i?["retention"]?.GetValue<string>() == "volatile_turn_only")
                {
                    if (i["text"] is not null) i["text"] = "[volatile]";
                    if (i["value"] is not null) i["value"] = "[volatile]";
                }
        var canonical = CanonicalJson(node);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool ContainsVolatile(PlanV3 plan)
        => plan.Items.Any(i => i.Retention == Retention.volatile_turn_only);

    /// <summary>
    /// Redacted correlation for telemetry when content-derived identifiers are unsafe:
    /// deployment-secret keyed HMAC-SHA256 with key-version metadata (rev-2 §3). Rows
    /// store "v{version}:{tag}" — rotation changes the version, never exposes content.
    /// </summary>
    public static string CorrelationTag(PlanV3 plan, byte[] deploymentKey, int keyVersion)
    {
        using var hmac = new HMACSHA256(deploymentKey);
        var tag = hmac.ComputeHash(Encoding.UTF8.GetBytes(CompactV3(plan)));
        return $"v{keyVersion}:{Convert.ToHexString(tag).ToLowerInvariant()}";
    }

    /// <summary>Canonical JSON: ordinal-sorted keys, compact, invariant number formatting.</summary>
    internal static string CanonicalJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteCanonical(node, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    WriteCanonical(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteCanonical(a[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                // Values may be element-backed (parsed) or CLR-backed (assigned in code,
                // e.g. the volatile-redaction placeholder); canonicalize both identically.
                if (v.TryGetValue<JsonElement>(out var el))
                    sb.Append(el.ValueKind switch
                    {
                        JsonValueKind.Number when el.TryGetInt64(out var l)
                            => l.ToString(CultureInfo.InvariantCulture),
                        JsonValueKind.Number
                            => el.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                        JsonValueKind.String => JsonSerializer.Serialize(el.GetString()),
                        _ => el.GetRawText(),
                    });
                else if (v.TryGetValue<string>(out var str))
                    sb.Append(JsonSerializer.Serialize(str));
                else if (v.TryGetValue<bool>(out var b))
                    sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<long>(out var l2))
                    sb.Append(l2.ToString(CultureInfo.InvariantCulture));
                else
                    sb.Append(v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture));
                break;
        }
    }

    // ---- protected v2 fallback (rev-2 §5) -----------------------------------------------

    /// <summary>
    /// Capability check BEFORE any v3→v2 translation: if translation would drop or weaken
    /// any obligation or protection, the plan is not v2-compatible and must be routed to a
    /// v3 renderer or fail diagnosed — never rendered knowingly incomplete. An INVALID v3
    /// plan is never v2-compatible (invalidity does not launder into fallback).
    /// </summary>
    public static V2Compatibility CheckV2Compatibility(PlanV3 plan)
    {
        var reasons = new List<string>();

        var structural = Validate(plan);
        if (structural.Count > 0)
            reasons.Add("plan is invalid; invalid v3 does not imply v2 is semantically safe");

        foreach (var i in plan.Items)
        {
            if (i.Retention != Retention.full)
                reasons.Add($"{i.Id}: retention={i.Retention} has no v2 carrier — protection would be silently lost");
            if (i.Disclosure == Disclosure.restricted)
                reasons.Add($"{i.Id}: restricted disclosure has no v2 carrier");
        }
        if (plan.RegisterRestrictions is { Count: > 0 })
            reasons.Add("registerRestrictions have no enforceable v2 carrier");

        return new V2Compatibility(reasons.Count == 0, reasons);
    }
}
