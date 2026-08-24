using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Companion.PlanV3;

/// <summary>
/// ResponsePlan v3 reference types (docs/RESPONSE_PLAN_V3_SPEC.md §2–§3).
/// Open sets (Type, Source, provenance origin) are strings; closed sets are enums
/// whose unknown wire values REJECT the item rather than guess an obligation.
/// </summary>
public sealed record PlanV3
{
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "plan/3";
    [JsonPropertyName("minorVersion")] public int MinorVersion { get; init; }
    [JsonPropertyName("traceId")] public Guid TraceId { get; init; }
    [JsonPropertyName("participants")] public required Participants Participants { get; init; }
    [JsonPropertyName("act")] public required string Act { get; init; }
    [JsonPropertyName("question")] public required QuestionPolicyBlock Question { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<PlanItem> Items { get; init; } = [];
    [JsonPropertyName("register")] public RegisterVector Register { get; init; } = new();

    /// <summary>Open extension blocks: preserved verbatim, never model-facing (spec §4.3).</summary>
    [JsonPropertyName("extensions")] public JsonObject? Extensions { get; init; }
}

public sealed record Participants(
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("companion")] string Companion);

public sealed record QuestionPolicyBlock(
    [property: JsonPropertyName("policy")] QuestionPolicy Policy,
    [property: JsonPropertyName("itemId")] string? ItemId = null);

[JsonConverter(typeof(JsonStringEnumConverter<QuestionPolicy>))]
public enum QuestionPolicy { ask_required, may_ask, question_forbidden }

[JsonConverter(typeof(JsonStringEnumConverter<ExpressionPolicy>))]
public enum ExpressionPolicy
{
    must_express, may_express, background_only, must_not_express,
    admit_unknown, ask_required, question_forbidden, style_guidance,
}

[JsonConverter(typeof(JsonStringEnumConverter<Sensitivity>))]
public enum Sensitivity { @public, personal, @private, never_store }

public sealed record PlanItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("policy")] public required ExpressionPolicy Policy { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("value")] public JsonNode? Value { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("provenance")] public Provenance? Provenance { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    [JsonPropertyName("sensitivity")] public Sensitivity Sensitivity { get; init; } = Sensitivity.personal;
    [JsonPropertyName("validity")] public Validity? Validity { get; init; }
    [JsonPropertyName("supersedes")] public IReadOnlyList<string>? Supersedes { get; init; }
    [JsonPropertyName("supersededBy")] public string? SupersededBy { get; init; }
    [JsonPropertyName("priority")] public int? Priority { get; init; }
    [JsonPropertyName("checkTokens")] public IReadOnlyList<string>? CheckTokens { get; init; }
}

public sealed record Provenance(
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("at")] DateTimeOffset? At = null,
    [property: JsonPropertyName("evidenceRef")] string? EvidenceRef = null);

public sealed record Validity(
    [property: JsonPropertyName("from")] DateTimeOffset? From = null,
    [property: JsonPropertyName("until")] DateTimeOffset? Until = null);

public sealed record RegisterVector
{
    [JsonPropertyName("warmth")] public string? Warmth { get; init; }
    [JsonPropertyName("bluntness")] public string? Bluntness { get; init; }
    [JsonPropertyName("playfulness")] public string? Playfulness { get; init; }
    [JsonPropertyName("teasing")] public string? Teasing { get; init; }
    [JsonPropertyName("skepticism")] public string? Skepticism { get; init; }
    [JsonPropertyName("intensity")] public string? Intensity { get; init; }
    [JsonPropertyName("verbosity")] public string? Verbosity { get; init; }
    [JsonPropertyName("profanity")] public string? Profanity { get; init; }
    [JsonPropertyName("mirror")] public bool? Mirror { get; init; }

    /// <summary>Free-form style prose carried losslessly from v2 (spec §8).</summary>
    [JsonPropertyName("legacyStyle")] public string? LegacyStyle { get; init; }
}

/// <summary>Outcome of a lenient parse: the plan, plus what was rejected/unknown (spec §4.3).</summary>
public sealed record ParseReport(
    PlanV3 Plan,
    IReadOnlyList<string> RejectedItems,
    IReadOnlyList<string> UnknownExtensionBlocks);
