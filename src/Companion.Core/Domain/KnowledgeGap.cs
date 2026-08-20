using System.Text.Json.Serialization;

namespace Companion.Core.Domain;

/// <summary>What kind of not-knowing this is (docs/KNOWLEDGE_GAPS.md §1). Two of the six
/// epistemic distinctions are deliberately NOT kinds: "I know this" is the absence of a
/// gap, and "not worth pursuing" is <see cref="GapStatus.Declined"/>.</summary>
[JsonConverter(typeof(KebabEnumConverter<GapKind>))]
public enum GapKind
{
    /// <summary>A concept she has been asked about (or that recurred) and never learned.</summary>
    UnknownConcept,

    /// <summary>She holds something about this, but thinly (learning-grade, low confidence).</summary>
    UncertainKnowledge,

    /// <summary>Evidence points both ways (a review-parked or disputed memory pair).</summary>
    ConflictingEvidence,

    /// <summary>A conversational reference the system could not pin.</summary>
    UnresolvedReference,
}

/// <summary>Which SYSTEM observed the gap. There is deliberately no value for the chat
/// model: interest is not evidence, and no code path lets model output mint a gap.</summary>
[JsonConverter(typeof(KebabEnumConverter<GapSource>))]
public enum GapSource
{
    /// <summary>The Phase-3 epistemic lookup ("do you know what X is?" → unknown/learning).</summary>
    KnowledgeLookup,

    /// <summary>Working context: a marker detected but unresolved, or a withheld guess.</summary>
    WorkingContext,

    /// <summary>The memory pipeline parked conflicting evidence for review.</summary>
    MemoryReview,
}

[JsonConverter(typeof(KebabEnumConverter<GapStatus>))]
public enum GapStatus
{
    /// <summary>Recorded, not yet pursued. Recording is NOT a promise to ask.</summary>
    Open,

    /// <summary>Promoted into the curiosity lifecycle — one curiosity per gap, ever.</summary>
    Pursuing,

    /// <summary>The answer arrived (e.g. the concept was taught); closed with provenance.</summary>
    Satisfied,

    /// <summary>Scored not worth pursuing — an epistemic statement in its own right.</summary>
    Declined,

    /// <summary>Aged out unpursued by the sleep-cycle sweep.</summary>
    Expired,
}

/// <summary>How the gap would be pursued if pursued. Only AskUser is live in v1;
/// Research is reserved against the future trusted-knowledge tool (whose output would
/// enter the store as KnowledgeOrigin.ToolVerified), Observe against the world link.</summary>
[JsonConverter(typeof(KebabEnumConverter<GapPursuit>))]
public enum GapPursuit { AskUser, Research, Observe, Defer }

/// <summary>
/// A typed piece of not-knowing, minted only from observable system state with provenance
/// — never from model output. The gap is the epistemic state; the question about it, if
/// one is ever warranted, lives in the EXISTING Curiosity subsystem, linked one-to-one.
/// Recurrence is salience: re-observing a gap bumps <see cref="Occurrences"/> instead of
/// creating rows, and never re-asks (the ask-once budget is inherited transitively).
/// </summary>
public class KnowledgeGap
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    public GapKind Kind { get; set; }

    /// <summary>The language handle ("quokka", the reference text) — normalized for dedupe
    /// with the same rule concept lookup uses.</summary>
    public string Subject { get; set; } = default!;

    /// <summary>Typed link when the gap is about a known Concept row.</summary>
    public Guid? SubjectConceptId { get; set; }

    public GapSource Source { get; set; }

    /// <summary>Provenance: the observing turn's TraceId, or a memory/assertion id.</summary>
    public Guid? SourceRef { get; set; }

    public int Occurrences { get; set; } = 1;
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public GapStatus Status { get; set; } = GapStatus.Open;
    public GapPursuit Pursuit { get; set; } = GapPursuit.AskUser;

    /// <summary>The curiosity this gap became, once promoted. One per gap, ever.</summary>
    public Guid? CuriosityId { get; set; }

    /// <summary>How it closed ("learned from teaching", "aged out").</summary>
    public string? ResolutionNote { get; set; }
}
