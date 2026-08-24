using System.Security.Cryptography;
using System.Text;
using Companion.PlanV3;

namespace Companion.Infrastructure.Renderer;

/// <summary>
/// The P3 shadow record for one turn's V3 plan (docs/RESPONSE_PLAN_V3_SPEC.md §14 P3):
/// hashes, validation, compatibility, and privacy-safe item metadata — NEVER protected
/// text. CompactV3 is not sent to any model; these rows test translation, serialization,
/// privacy, and infrastructure only, and `translated_v2` rows are never native-V3 corpus
/// examples.
/// </summary>
public sealed record V3ShadowEnvelope
{
    /// <summary>Semantic origin. Until the planner constructs V3 without first building
    /// ResponsePlan V2, this is always "translated_v2" — V3 is an intermediary here.</summary>
    public required string PlanOrigin { get; init; }

    public required string Protocol { get; init; }
    public required string V2SourceHash { get; init; }
    public required string WirePlanHash { get; init; }

    /// <summary>Null when the plan contains protected content (content-derived).</summary>
    public string? RenderPromptHash { get; init; }

    /// <summary>Keyed versioned tag; present only for protected plans with a configured key.</summary>
    public string? CorrelationTag { get; init; }

    public required bool Valid { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public required bool V2Compatible { get; init; }
    public IReadOnlyList<string> V2IncompatibilityReasons { get; init; } = [];
    public required bool AudienceOk { get; init; }
    public IReadOnlyList<string> AudienceErrors { get; init; } = [];
    public IReadOnlyList<string> AudienceExcludedItemIds { get; init; } = [];

    public required bool ContainsProtected { get; init; }
    public required int ItemCount { get; init; }
    public required int RedactedItemCount { get; init; }
    public IReadOnlyList<string> UnknownExtensionBlocks { get; init; } = [];

    /// <summary>Per-item metadata; Text present ONLY for unprotected, full-retention items.</summary>
    public IReadOnlyList<V3ShadowItem> Items { get; init; } = [];

    // ---- P4: the native_v3 sibling, recorded beside the translation ----

    /// <summary>"native_v3" section for the same turn; null when the native build failed.</summary>
    public V3NativeSection? Native { get; init; }

    /// <summary>Content-safe native build failure (exception type + message head).</summary>
    public string? NativeBuildError { get; init; }

    /// <summary>Content-safe source-side lint rejections ("id source rule").</summary>
    public IReadOnlyList<string> NativeLintRejections { get; init; } = [];

    /// <summary>Semantic parity by class; differences are evidence, never behavior.</summary>
    public IReadOnlyList<V3ParityClass> Parity { get; init; } = [];
}

public sealed record V3NativeSection(
    string PlanOrigin,            // always "native_v3"
    string WirePlanHash,
    string? RenderPromptHash,
    string? CorrelationTag,
    bool Valid,
    IReadOnlyList<string> ValidationErrors,
    bool AudienceOk,
    int ItemCount,
    int RedactedItemCount,
    IReadOnlyDictionary<string, int> PolicyCounts,
    IReadOnlyDictionary<string, int> CategoryCounts,
    string RegisterLine);

public sealed record V3ParityClass(string Class, string Status, IReadOnlyList<string> Details);

public sealed record V3ShadowItem(
    string Id, string Type, string Policy, string Source, string Category,
    string Disclosure, string Retention, bool Redacted, string? Text);

public static class V3ShadowEnvelopeBuilder
{
    /// <summary>
    /// Builds the recordable envelope, applying the complete V3 disclosure/retention rules
    /// BEFORE anything persists: an item's text is included only when disclosure is
    /// unrestricted/participants AND retention is full; everything else records metadata
    /// plus a redaction marker. Validation/extension events carry names and reason codes,
    /// never protected values.
    /// </summary>
    public static V3ShadowEnvelope Build(
        Companion.Core.Domain.ResponsePlan v2Plan,
        Companion.PlanV3.PlanV3 v3,
        byte[]? correlationKey,
        int correlationKeyVersion,
        IReadOnlyCollection<string> currentRecipientPrincipals,
        RendererTrustContext trust)
    {
        var v2Bytes = Companion.RendererBench.PlanSerialization.CompactV2(v2Plan);
        var v2Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v2Bytes))).ToLowerInvariant();

        var validation = PlanV3Codec.Validate(v3);
        var compat = PlanV3Codec.CheckV2Compatibility(v3);
        var audience = PlanV3Codec.ValidateForAudience(v3, currentRecipientPrincipals, trust);
        var containsProtected = PlanV3Codec.ContainsProtectedContent(v3);
        var identity = PlanV3Codec.PersistableIdentity(v3, correlationKey, correlationKeyVersion);

        var items = new List<V3ShadowItem>();
        var redacted = 0;
        foreach (var i in v3.Items)
        {
            var itemProtected = i.Disclosure == Disclosure.restricted || i.Retention != Retention.full;
            if (itemProtected)
                redacted++;
            items.Add(new V3ShadowItem(
                i.Id, i.Type, i.Policy.ToString(), i.Source,
                PlanV3Codec.CategoryOf(i).ToString(),
                i.Disclosure.ToString(), i.Retention.ToString(),
                Redacted: itemProtected,
                Text: itemProtected ? null : i.Text));
        }

        return new V3ShadowEnvelope
        {
            PlanOrigin = "translated_v2",
            Protocol = v3.Protocol,
            V2SourceHash = v2Hash,
            WirePlanHash = identity.WirePlanHash,
            RenderPromptHash = identity.RenderPromptHash,
            CorrelationTag = identity.CorrelationTag,
            Valid = validation.Count == 0,
            ValidationErrors = validation,
            V2Compatible = compat.Compatible,
            V2IncompatibilityReasons = compat.Reasons,
            AudienceOk = audience.Ok,
            AudienceErrors = audience.Errors,
            AudienceExcludedItemIds = audience.ExcludedItemIds,
            ContainsProtected = containsProtected,
            ItemCount = v3.Items.Count,
            RedactedItemCount = redacted,
            UnknownExtensionBlocks = v3.Extensions?.Select(kv => kv.Key).ToList() ?? [],
            Items = items,
        };
    }

    /// <summary>P4: the native_v3 section — same privacy rules, plus semantic parity.</summary>
    public static V3ShadowEnvelope WithNative(
        V3ShadowEnvelope envelope,
        Companion.PlanV3.PlanV3 translated,
        Companion.PlanV3.PlanV3? native,
        string? nativeBuildError,
        IReadOnlyList<string> nativeLintRejections,
        byte[]? correlationKey,
        int correlationKeyVersion,
        IReadOnlyCollection<string> currentRecipientPrincipals,
        RendererTrustContext trust)
    {
        if (native is null)
            return envelope with
            {
                NativeBuildError = nativeBuildError ?? "native build unavailable",
                NativeLintRejections = nativeLintRejections,
            };

        var validation = PlanV3Codec.Validate(native);
        var audience = PlanV3Codec.ValidateForAudience(native, currentRecipientPrincipals, trust);
        var identity = PlanV3Codec.PersistableIdentity(native, correlationKey, correlationKeyVersion);
        var redacted = native.Items.Count(i =>
            i.Disclosure == Disclosure.restricted || i.Retention != Retention.full);
        var reg = PlanV3Codec.Canonicalize(native.Register);

        var parity = PlanParity.Compare(translated, native).Classes
            .Select(c => new V3ParityClass(c.Class, c.Status, c.Details))
            .ToList();

        return envelope with
        {
            Native = new V3NativeSection(
                "native_v3",
                identity.WirePlanHash,
                identity.RenderPromptHash,
                identity.CorrelationTag,
                validation.Count == 0,
                validation,
                audience.Ok,
                native.Items.Count,
                redacted,
                native.Items.GroupBy(i => i.Policy.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                native.Items.GroupBy(i => PlanV3Codec.CategoryOf(i).ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                $"warmth={reg.Warmth} bluntness={reg.Bluntness} playful={reg.Playfulness} "
                + $"verbosity={reg.Verbosity} profanity={reg.Profanity} mirror={reg.Mirror}"),
            NativeLintRejections = nativeLintRejections,
            Parity = parity,
        };
    }
}
