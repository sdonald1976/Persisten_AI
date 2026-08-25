using System.Text.Json.Serialization;

namespace Companion.PlanV3;

/// <summary>
/// The plan/4 fiction frame: the declared interpretive mode for a turn.
///
/// It changes how the rest of the plan is READ and never what is true. A fictional action
/// may be narrated inside the frame; it never becomes a claim that the real person performed
/// it, never reaches real memory, and never grants authorization to anybody.
///
/// Optional by construction. A null frame is an ordinary real turn, serializes no FRAME
/// section, and costs zero tokens — which is why plan/4 can be the single protocol for both
/// real and fiction turns.
///
/// Deliberately absent: any content classification. There is no rating, contentClass or
/// intensity, and none may be added. Sexual content, profanity, romance, darkness and
/// violence are ordinary possible fictional content; a restriction exists only when an
/// explicit user boundary (<see cref="Boundaries"/>) or explicit hosting configuration backs
/// it.
/// </summary>
public sealed record Frame
{
    [JsonPropertyName("mode")] public required FrameMode Mode { get; init; }

    [JsonPropertyName("transition")] public required FrameTransition Transition { get; init; }

    /// <summary>Scene identity. NOT a store — see the continuity note on <see cref="Continuity"/>.</summary>
    [JsonPropertyName("sceneRef")] public string? SceneRef { get; init; }

    [JsonPropertyName("narration")] public FrameNarration Narration { get; init; } = FrameNarration.forbidden;

    /// <summary>
    /// TRANSCRIPT-WINDOW continuity only. `maintain` asks the mouth to stay consistent with
    /// the window it can see. <see cref="SceneRef"/> says "the same scene as before"; it
    /// cannot retrieve what happened in it, because scene content is deliberately not
    /// persisted. Resuming a scene from a previous session is out of scope by design.
    /// </summary>
    [JsonPropertyName("continuity")] public FrameContinuity Continuity { get; init; } = FrameContinuity.none;

    /// <summary>
    /// The character the companion is currently speaking AS. Optional and explicit: absent
    /// means she is narrating rather than voicing anyone. Never derived from
    /// <see cref="FrameCharacter.ControlledBy"/> uniqueness — one participant may control
    /// several characters, which is ordinary roleplay.
    /// </summary>
    [JsonPropertyName("activeCompanionCharacterId")] public string? ActiveCompanionCharacterId { get; init; }

    [JsonPropertyName("narrator")] public FrameNarrator? Narrator { get; init; }

    [JsonPropertyName("characters")] public IReadOnlyList<FrameCharacter> Characters { get; init; } = [];

    /// <summary>Scene-scoped user boundaries. Each cites a FrameBoundaryRecord.</summary>
    [JsonPropertyName("boundaries")] public IReadOnlyList<FrameBoundaryRef> Boundaries { get; init; } = [];
}

/// <summary>
/// A frame-local character. <see cref="CharacterId"/> is namespaced away from authorization
/// entirely: it may never appear in an item's audience, owner, or any recipient set.
/// Authorization is not a costume.
/// </summary>
public sealed record FrameCharacter(
    [property: JsonPropertyName("characterId")] string CharacterId,
    [property: JsonPropertyName("display")] string Display,
    /// <summary>The participant playing this character, or null for an unvoiced NPC.
    /// May repeat: one participant can control several characters.</summary>
    [property: JsonPropertyName("controlledBy")] string? ControlledBy = null);

/// <summary>
/// Who narrates, and whose perspective the reader occupies. These are separate because
/// third-person limited — the commonest mode in prose fiction — has an EXTERNAL narrator
/// and a viewpoint character, and collapsing them makes that mode inexpressible.
/// </summary>
public sealed record FrameNarrator(
    [property: JsonPropertyName("kind")] NarratorKind Kind,
    /// <summary>Required when Kind is character; forbidden when external.</summary>
    [property: JsonPropertyName("characterId")] string? CharacterId = null,
    /// <summary>Whose perspective. Optional; absent means omniscient or unspecified.</summary>
    [property: JsonPropertyName("viewpointCharacterId")] string? ViewpointCharacterId = null,
    [property: JsonPropertyName("person")] NarrativePerson Person = NarrativePerson.third);

/// <summary>A scene-scoped boundary the user stated, citing its evidence record.</summary>
public sealed record FrameBoundaryRef(
    [property: JsonPropertyName("boundaryId")] string BoundaryId,
    /// <summary>What the user asked for, as stated. Named on the wire so the mouth can obey it.</summary>
    [property: JsonPropertyName("subject")] string Subject,
    /// <summary>FrameBoundaryRecord.Id. A boundary without one is rejected.</summary>
    [property: JsonPropertyName("evidenceRef")] string? EvidenceRef = null);

[JsonConverter(typeof(JsonStringEnumConverter<FrameMode>))]
public enum FrameMode { real, fiction }

[JsonConverter(typeof(JsonStringEnumConverter<FrameTransition>))]
public enum FrameTransition { enter, @continue, switchScene, exit }

[JsonConverter(typeof(JsonStringEnumConverter<FrameNarration>))]
public enum FrameNarration { forbidden, licensed }

[JsonConverter(typeof(JsonStringEnumConverter<FrameContinuity>))]
public enum FrameContinuity { none, maintain }

[JsonConverter(typeof(JsonStringEnumConverter<NarratorKind>))]
public enum NarratorKind { character, external }

[JsonConverter(typeof(JsonStringEnumConverter<NarrativePerson>))]
public enum NarrativePerson { first, second, third }
