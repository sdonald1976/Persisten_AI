using System.Text.Json.Serialization;

namespace Companion.Core.Domain;

/// <summary>What kind of acknowledgment a turn owes.</summary>
[JsonConverter(typeof(KebabEnumConverter<AckKind>))]
public enum AckKind { CorrectionAccepted, FactTaught, AnswerReceived }

/// <summary>Whose error a correction addresses — the Mad Hatter field. When the system
/// knows the owner, "we both slipped up" is a fidelity violation, not a style choice.</summary>
[JsonConverter(typeof(KebabEnumConverter<ErrorOwner>))]
public enum ErrorOwner { Companion, User, Nobody }

[JsonConverter(typeof(KebabEnumConverter<ContentKind>))]
public enum ContentKind { LearnedKnowledge, Memory, SharedMemory, ToolResult, Interpretation, SelfState }

/// <summary>Content authority. MustState is owed to the user; MayUse is the palette;
/// MustNotContradict is history that stays history.</summary>
[JsonConverter(typeof(KebabEnumConverter<ContentRequirement>))]
public enum ContentRequirement { MustState, MayUse, MustNotContradict }

[JsonConverter(typeof(KebabEnumConverter<EpistemicKind>))]
public enum EpistemicKind { NotLearned, Uncertain, Disputed }

[JsonConverter(typeof(KebabEnumConverter<QuestionKind>))]
public enum QuestionKind { Clarify, Curiosity }

/// <summary>Something this turn must acknowledge, with its decided semantics.</summary>
public sealed record Acknowledgment(
    [property: JsonConverter(typeof(KebabEnumConverter<AckKind>))] AckKind Kind,
    [property: JsonConverter(typeof(KebabEnumConverter<ErrorOwner>))] ErrorOwner ErrorOwner,
    string Text);

/// <summary>One piece of content with its authority level and provenance label.</summary>
public sealed record PlannedContent(
    [property: JsonConverter(typeof(KebabEnumConverter<ContentKind>))] ContentKind Kind,
    [property: JsonConverter(typeof(KebabEnumConverter<ContentRequirement>))] ContentRequirement Requirement,
    string Text,
    string? Provenance = null);

/// <summary>A typed epistemic constraint: what she does not know, holds thinly, or disputes.</summary>
public sealed record EpistemicNote(
    [property: JsonConverter(typeof(KebabEnumConverter<EpistemicKind>))] EpistemicKind Kind,
    string Subject);

/// <summary>The question this turn may (or must) ask. Clarify is mandatory when selected;
/// a curiosity is never mandatory — the ask-once budget already spent it either way.</summary>
public sealed record PlannedQuestion(
    [property: JsonConverter(typeof(KebabEnumConverter<QuestionKind>))] QuestionKind Kind,
    string Text,
    bool Mandatory);

/// <summary>Style guidance — the renderer's side of the boundary, carried not commanded.</summary>
public sealed record ToneGuidance(string? Register, string? MoodNote, string? PersonaStyle);

/// <summary>
/// What Ava has DECIDED this turn — the typed boundary between cognition and prose
/// (docs/RESPONSE_PLAN.md). Every field is populated from state the pipeline already
/// computes; the plan consolidates it with authority levels. SHADOW in stage 1: computed
/// and recorded beside every turn, changing nothing, so its fidelity to desirable
/// conversation is measured before it is ever rendered. This record, serialized, is the
/// future input contract for a language-only renderer:
/// plan + transcript window → faithful natural-language utterance.
/// </summary>
public sealed record ResponsePlan
{
    public Guid TraceId { get; init; }

    /// <summary>The selected conversational act — TurnIntent, reused not re-modeled.</summary>
    [JsonConverter(typeof(KebabEnumConverter<TurnIntent>))]
    public required TurnIntent Act { get; init; }

    public IReadOnlyList<Acknowledgment> Acknowledgments { get; init; } = Array.Empty<Acknowledgment>();

    public IReadOnlyList<PlannedContent> Content { get; init; } = Array.Empty<PlannedContent>();

    public IReadOnlyList<EpistemicNote> Epistemic { get; init; } = Array.Empty<EpistemicNote>();

    public PlannedQuestion? Question { get; init; }

    public required ToneGuidance Tone { get; init; }
}
