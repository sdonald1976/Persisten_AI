using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Companion.PlanV3;

/// <summary>
/// ResponsePlan v3 reference types (docs/RESPONSE_PLAN_V3_SPEC.md revision 2).
/// Closed-set wire casing is snake_case; open-set strings and model-facing CompactV3
/// labels are kebab-case (§6-rev2). Unknown values in closed sets invalidate the WHOLE
/// plan. Display names are labels, never authorization identifiers (§1-rev2).
/// </summary>
public sealed record PlanV3
{
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "plan/3";
    [JsonPropertyName("minorVersion")] public int MinorVersion { get; init; }
    [JsonPropertyName("traceId")] public Guid TraceId { get; init; }

    /// <summary>Stable participant identities. Ids authorize; displays label.</summary>
    [JsonPropertyName("participants")] public required IReadOnlyList<Participant> Participants { get; init; }

    [JsonPropertyName("act")] public required string Act { get; init; }
    [JsonPropertyName("question")] public required QuestionPolicyBlock Question { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<PlanItem> Items { get; init; } = [];
    [JsonPropertyName("register")] public RegisterVector Register { get; init; } = new();
    [JsonPropertyName("registerRestrictions")]
    public IReadOnlyList<RegisterRestriction>? RegisterRestrictions { get; init; }
    [JsonPropertyName("budget")] public Budget? Budget { get; init; }
    [JsonPropertyName("extensions")] public JsonObject? Extensions { get; init; }

    /// <summary>
    /// plan/4: the OPTIONAL fiction frame. Null on every ordinary turn, and null is the
    /// default, so plan/3 plans are unchanged in meaning and in serialization —
    /// CompactV3 never sees it and the corpus goldens are untouched.
    /// </summary>
    [JsonPropertyName("frame")] public Frame? Frame { get; init; }
}

/// <summary>
/// A stable principal in the conversation. `Id` is the authorization identifier
/// (stable across display-name changes); `Display` is presentation only.
/// </summary>
public sealed record Participant(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] ParticipantRole Role,
    [property: JsonPropertyName("display")] string Display);

[JsonConverter(typeof(JsonStringEnumConverter<ParticipantRole>))]
public enum ParticipantRole { user, companion, other }

public sealed record QuestionPolicyBlock(
    [property: JsonPropertyName("policy")] QuestionPolicy Policy,
    [property: JsonPropertyName("itemId")] string? ItemId = null);

[JsonConverter(typeof(JsonStringEnumConverter<QuestionPolicy>))]
public enum QuestionPolicy { ask_required, may_ask, question_forbidden }

/// <summary>
/// SIX policies (rev-2 §4): question prohibition is owned solely by question.policy and
/// style solely by the RegisterVector — the former item-level duplicates
/// (question_forbidden, style_guidance) are removed to eliminate parallel authorities.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExpressionPolicy>))]
public enum ExpressionPolicy
{
    must_express, may_express, background_only, must_not_express,
    admit_unknown, ask_required,
}

[JsonConverter(typeof(JsonStringEnumConverter<RenderCategory>))]
public enum RenderCategory
{
    claim, memory, shared_memory, knowledge, correction, agreement, teaching,
    answer, clarify, curiosity, boundary, superseded, state, observation, note,
}

[JsonConverter(typeof(JsonStringEnumConverter<Classification>))]
public enum Classification { @public, personal, @private, intimate }

/// <summary>
/// unrestricted/participants need no audience list; restricted REQUIRES an explicit
/// audience of principal references (§1-rev2). "owner_only" is gone — ownership and
/// audience are separate facts, and the owner may be a third party not present.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Disclosure>))]
public enum Disclosure { unrestricted, participants, restricted }

[JsonConverter(typeof(JsonStringEnumConverter<Retention>))]
public enum Retention { full, no_training, no_telemetry_text, volatile_turn_only }

public sealed record PlanItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("category")] public RenderCategory? Category { get; init; }
    [JsonPropertyName("policy")] public required ExpressionPolicy Policy { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("quoted")] public bool Quoted { get; init; }
    [JsonPropertyName("value")] public JsonNode? Value { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("provenance")] public Provenance? Provenance { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }

    [JsonPropertyName("classification")] public Classification Classification { get; init; } = Classification.personal;
    [JsonPropertyName("disclosure")] public Disclosure Disclosure { get; init; } = Disclosure.participants;

    /// <summary>
    /// Whose information this is: an in-plan participant id, or an external principal
    /// reference with an explicit scheme ("principal:scott-father"). Information about a
    /// third party is not owned by whoever happened to supply it (§1-rev2).
    /// </summary>
    [JsonPropertyName("owner")] public string? Owner { get; init; }

    /// <summary>Explicit authorized audience; REQUIRED when disclosure=restricted. Entries
    /// are in-plan participant ids or scheme-prefixed external principal refs.</summary>
    [JsonPropertyName("audience")] public IReadOnlyList<string>? Audience { get; init; }

    [JsonPropertyName("retention")] public Retention Retention { get; init; } = Retention.full;
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
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

public sealed record RegisterRestriction(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("provenance")] Provenance? Provenance = null);

public sealed record Budget(
    [property: JsonPropertyName("maxItems")] int? MaxItems = null,
    [property: JsonPropertyName("dropOrder")] IReadOnlyList<DropCategory>? DropOrder = null);

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
    [JsonPropertyName("profanity")] public string? Profanity { get; init; }
    [JsonPropertyName("mirror")] public bool? Mirror { get; init; }

    /// <summary>MIGRATION METADATA ONLY (rev-2 §6): v2 tone prose carried for lossless
    /// v2→v3→v2 round-trips. NEVER enters CompactV3 (tested).</summary>
    [JsonPropertyName("legacyStyle")] public string? LegacyStyle { get; init; }
}

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

/// <summary>Outcome of the v2-capability check (rev-2 §5): translation is all-or-nothing.</summary>
public sealed record V2Compatibility(bool Compatible, IReadOnlyList<string> Reasons);

/// <summary>The renderer's processing context (rev-2.1 §2): where the plan's bytes go.</summary>
public sealed record RendererTrustContext(
    RendererTransport Transport, string? ProcessingContextId = null);

[JsonConverter(typeof(JsonStringEnumConverter<RendererTransport>))]
public enum RendererTransport { local_loopback, trusted_remote, untrusted }

/// <summary>
/// Recipient-aware authorization decision (rev-2.1 §2). Errors are fatal (an obligation
/// cannot legally reach the recipient — replan upstream or fail diagnosed, never drop or
/// downgrade). ExcludedItemIds are non-obligation items lawfully withheld from this
/// renderer/recipient — protected content is not leaked merely to prohibit it.
/// </summary>
public sealed record AudienceDecision(
    bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> ExcludedItemIds);

/// <summary>
/// What may be PERSISTED about a plan (rev-2.1 §3). WirePlanHash is the redacted
/// STRUCTURAL hash — never a unique content identity for protected plans.
/// RenderPromptHash is null for protected plans (content-derived). CorrelationTag is the
/// keyed, versioned identity for protected plans: distinct texts stay distinguishable
/// without enabling offline dictionaries.
/// </summary>
public sealed record PlanIdentity(
    string WirePlanHash, string? RenderPromptHash, string? CorrelationTag);
