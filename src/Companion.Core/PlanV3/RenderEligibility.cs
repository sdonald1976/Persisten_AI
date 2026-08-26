namespace Companion.PlanV3;

/// <summary>
/// The typed reason a structurally valid plan still cannot be serialized for a renderer.
///
/// These are deliberately NOT validation errors. <see cref="PlanV3Codec.Validate"/> answers
/// "is this document well formed?"; render eligibility answers "may these items be turned into
/// model-facing bytes?". A plan can pass the first and fail the second, and before this type
/// existed the only way to discover that was to catch an exception out of the serializer.
/// </summary>
public static class RenderRefusalCodes
{
    /// <summary>
    /// Producer-authored text that coaches the renderer. Provenance-aware by design: quoted
    /// items and non-authored sources are exempt, because quoting makes the text DATA rather
    /// than instruction (see <see cref="PlanV3Codec.CoachingViolation"/>).
    /// </summary>
    public const string ProducerCoaching = "producer-coaching";
}

/// <summary>
/// One typed refusal. Content-safe BY CONSTRUCTION: it carries the item id, the source, and
/// the rule — never the offending text. This is the same shape the source-side lint rejections
/// already use (<c>"{id} source={source} rule={code}"</c>), so a refusal recorded here and a
/// rejection recorded at assembly read identically, and neither can leak producer text into a
/// shadow row, a log line, or an exception message.
/// </summary>
public sealed record RenderRefusal(string ItemId, string Source, string Code)
{
    public override string ToString() => $"{ItemId} source={Source} rule={Code}";
}

/// <summary>
/// The answer to "may this plan be serialized?", asked and answered without throwing.
///
/// Callers that intend to serialize should consult this FIRST and record
/// <see cref="Refusals"/> when it says no. <see cref="PlanV3Codec.CompactV3"/> and
/// <see cref="PlanV4Codec.CompactV4"/> both require it, so the serializer cannot apply a rule
/// that a caller had no way to ask about.
/// </summary>
public sealed record RenderEligibility(IReadOnlyList<RenderRefusal> Refusals)
{
    public bool Eligible => Refusals.Count == 0;

    public static RenderEligibility Ok { get; } = new([]);

    /// <summary>The refusals as wire strings, in plan-item order. Content-safe.</summary>
    public IReadOnlyList<string> Reasons => Refusals.Select(r => r.ToString()).ToList();
}

/// <summary>
/// Thrown when serialization is asked for a plan that render eligibility already refused.
///
/// It derives from <see cref="InvalidOperationException"/> so that every existing caller and
/// test that catches the base type keeps working unchanged; what is new is that the refusal
/// reasons are now <em>typed and reachable</em> rather than only formatted into a message.
/// </summary>
public sealed class PlanNotRenderableException(RenderEligibility eligibility)
    : InvalidOperationException(
        "plan is not render-eligible: " + string.Join("; ", eligibility.Reasons))
{
    public RenderEligibility Eligibility { get; } = eligibility;

    public IReadOnlyList<RenderRefusal> Refusals => Eligibility.Refusals;
}
