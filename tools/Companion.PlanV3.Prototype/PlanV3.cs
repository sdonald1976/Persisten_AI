using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Companion.PlanV3;

/// <summary>
/// ResponsePlan v3 reference types (docs/RESPONSE_PLAN_V3_SPEC.md §2–§3, revision 1).
/// Open sets (Type, Source, provenance origin, reason codes within their families) are
/// strings; closed sets are enums. An unknown wire value in a CLOSED set invalidates the
/// WHOLE plan (spec §4.3) — an obligation is never guessed and never silently dropped.
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

    /// <summary>
    /// Restrictive register settings must be owned: any non-default restrictive value
    /// (profanity avoid/forbidden, teasing off when relationship allowed it, …) requires an
    /// entry here naming owner + reason code (spec §1-resolution). No unnamed authority.
    /// </summary>
    [JsonPropertyName("registerRestrictions")]
    public IReadOnlyList<RegisterRestriction>? RegisterRestrictions { get; init; }

    [JsonPropertyName("budget")] public Budget? Budget { get; init; }

    /// <summary>
    /// Open extension blocks: SEMANTICALLY preserved (canonical re-serialization, JSON value
    /// equality — NOT raw-byte identity; spec §4.4), never model-facing, diagnostics-visible.
    /// All minor-version additive data enters here (spec §4.5).
    /// </summary>
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

/// <summary>
/// CLOSED model-facing rendering vocabulary (spec §10-resolution). CompactV3 prints ONLY
/// these labels; the open semantic `type` never reaches the prompt, so a new source with
/// known semantics introduces no unfamiliar control vocabulary and owes no retraining.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RenderCategory>))]
public enum RenderCategory
{
    claim, memory, shared_memory, knowledge, correction, agreement, teaching,
    answer, clarify, curiosity, boundary, superseded, state, observation, note,
}

/// <summary>Label only — carries no behavior (spec §2-resolution; behavior split below).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Classification>))]
public enum Classification { @public, personal, @private, intimate }

/// <summary>Who the content may be disclosed to, independent of storage and expression.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Disclosure>))]
public enum Disclosure { unrestricted, participants, owner_only }

/// <summary>
/// Storage/logging policy, independent of expression: volatile content may still be
/// must_express to its authorized audience — it just never lands in telemetry text,
/// training exports, or long-term memory.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Retention>))]
public enum Retention { full, no_training, no_telemetry_text, volatile_turn_only }

public sealed record PlanItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>OPEN semantic type for diagnostics/attribution; never model-facing.</summary>
    [JsonPropertyName("type")] public required string Type { get; init; }

    /// <summary>Closed model-facing label; when absent, derived deterministically from policy.</summary>
    [JsonPropertyName("category")] public RenderCategory? Category { get; init; }

    [JsonPropertyName("policy")] public required ExpressionPolicy Policy { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>Text is verbatim third-party/user/tool content, exempt from the coaching
    /// lint; requires provenance.origin in the quoted-capable set (validated).</summary>
    [JsonPropertyName("quoted")] public bool Quoted { get; init; }

    [JsonPropertyName("value")] public JsonNode? Value { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("provenance")] public Provenance? Provenance { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }

    [JsonPropertyName("classification")] public Classification Classification { get; init; } = Classification.personal;
    [JsonPropertyName("disclosure")] public Disclosure Disclosure { get; init; } = Disclosure.participants;
    [JsonPropertyName("retention")] public Retention Retention { get; init; } = Retention.full;

    /// <summary>
    /// REQUIRED for restrictive policies (must_not_express): a kebab reason code within the
    /// permitted restriction families — user-preference.*, privacy-audience.*,
    /// tool-authorization.*, epistemic-integrity.*, hosting-config.*. No general
    /// moral-content authority exists (spec §1-resolution).
    /// </summary>
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }

    [JsonPropertyName("validity")] public Validity? Validity { get; init; }

    /// <summary>Item-id refs must resolve in-plan; external refs use a scheme prefix
    /// ("memory:", "concept:", …) and are explicitly external (validated).</summary>
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

public sealed record RegisterRestriction(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("provenance")] Provenance? Provenance = null);

public sealed record Budget(
    [property: JsonPropertyName("maxItems")] int? MaxItems = null,
    [property: JsonPropertyName("dropOrder")] IReadOnlyList<DropCategory>? DropOrder = null);

/// <summary>CLOSED drop vocabulary; obligations are not in it and therefore undroppable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DropCategory>))]
public enum DropCategory { background_only, may_express, style_detail }

public sealed record RegisterVector
{
    [JsonPropertyName("warmth")] public string? Warmth { get; init; }
    [JsonPropertyName("bluntness")] public string? Bluntness { get; init; }
    [JsonPropertyName("playfulness")] public string? Playfulness { get; init; }
    [JsonPropertyName("teasing")] public string? Teasing { get; init; }
    [JsonPropertyName("skepticism")] public string? Skepticism { get; init; }
    [JsonPropertyName("intensity")] public string? Intensity { get; init; }
    [JsonPropertyName("verbosity")] public string? Verbosity { get; init; }

    /// <summary>unrestricted | mirror-only | encouraged | neutral | avoid | forbidden.
    /// avoid/forbidden REQUIRE a RegisterRestriction entry naming owner + reason
    /// (user-preference.* or hosting-config.* only).</summary>
    [JsonPropertyName("profanity")] public string? Profanity { get; init; }

    [JsonPropertyName("mirror")] public bool? Mirror { get; init; }
    [JsonPropertyName("legacyStyle")] public string? LegacyStyle { get; init; }
}

/// <summary>
/// Parse outcome (spec §4.3): EITHER a valid plan, OR invalid with reasons — never a
/// partially-honored plan. Unknown extension blocks are observability data, not errors.
/// </summary>
public sealed record ParseReport(
    PlanV3? Plan,
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> UnknownExtensionBlocks)
{
    public PlanV3 ValidPlan => Valid && Plan is not null
        ? Plan
        : throw new InvalidOperationException("plan is invalid: " + string.Join("; ", Errors));
}
