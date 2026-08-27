using System.Text.Json.Serialization;

namespace Companion.MouthFactory.Schema;

/// <summary>
/// Scenario truth: the structured hidden state a row is generated FROM and evaluated AGAINST.
///
/// This is the first of the three representations the factory keeps strictly apart:
///
///   1. scenario truth  — this type. Rich, versioned, never shown to the model.
///   2. native Plan/4   — built through the real PlanV3/PlanV4 types and validators.
///   3. training row    — the exact bytes MouthPromptV4 produces, plus the target only.
///
/// Keeping (1) rich is what makes deterministic evaluation possible: "did the reply state the
/// facts it had to, avoid the ones it must not, and preserve the ambiguity" is answerable from
/// structure. Asking a second language model whether something "sounds right" is not evaluation,
/// and every check that can be made mechanically is made here instead.
///
/// It is deliberately NOT a competing mouth protocol. Nothing in this type reaches the model
/// except by way of the native plan the factory constructs from it.
/// </summary>
public sealed record ScenarioTruth
{
    /// <summary>Bumped when the shape changes. Recorded on every row and manifest.</summary>
    public const string SchemaVersion = "scenario/1.0";

    [JsonPropertyName("schemaVersion")] public string Version { get; init; } = SchemaVersion;

    /// <summary>Stable across regeneration: derived from the family and the seed, never random.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The family this scenario belongs to (R5 stratum id: "a6c", "b11", …). Splitting is by
    /// family, so every near-variant of a scenario lands in the same split by construction.
    /// </summary>
    public required string FamilyId { get; init; }

    /// <summary>
    /// The scenario family this is a VARIANT of. All targets and paraphrases of one hidden state
    /// share this, and it is the unit the splitter partitions on.
    /// </summary>
    public required string ScenarioFamilyId { get; init; }

    /// <summary>Layer A (language and voice) or Layer B (Plan/4 control and fidelity).</summary>
    public required CurriculumLayer Layer { get; init; }

    // ---- participants and stable identity ------------------------------------------------------

    public required IReadOnlyList<Participant> Participants { get; init; }

    // ---- what is true, and what may be said about it -------------------------------------------

    /// <summary>Facts upstream has approved. The mouth may express these and nothing beyond them.</summary>
    public IReadOnlyList<ApprovedFact> ApprovedFacts { get; init; } = [];

    /// <summary>
    /// Facts that WERE true and have been superseded. The evaluator checks these do not
    /// resurrect — the failure mode where a correction is acknowledged and then ignored.
    /// </summary>
    public IReadOnlyList<Supersession> Superseded { get; init; } = [];

    /// <summary>Things the scenario deliberately leaves open. Silently resolving one is a defect.</summary>
    public IReadOnlyList<string> IntentionalAmbiguities { get; init; } = [];

    /// <summary>Things upstream does NOT know. The reply must admit them, never explain them.</summary>
    public IReadOnlyList<string> EpistemicUnknowns { get; init; } = [];

    // ---- the conversation --------------------------------------------------------------------

    /// <summary>Prior turns, oldest first. Becomes the transcript window verbatim.</summary>
    public IReadOnlyList<Turn> History { get; init; } = [];

    public required string UserMessage { get; init; }

    // ---- controls -----------------------------------------------------------------------------

    public required RegisterControls Register { get; init; }

    public QuestionPolicySpec Question { get; init; } = new();

    /// <summary>Fiction-frame state, when the scenario is inside one. Null outside fiction.</summary>
    public FrameState? Frame { get; init; }

    // ---- the assertions the evaluator makes ---------------------------------------------------

    /// <summary>
    /// Propositions the reply MUST convey (in any wording). Checked structurally, not by string
    /// match — a target that omits one is rejected before any critic is consulted.
    /// </summary>
    public IReadOnlyList<Proposition> ExpectedPropositions { get; init; } = [];

    /// <summary>
    /// Propositions the reply must NOT convey: unsupported specifics the base model is tempted
    /// to supply ("a red vehicle" becoming "a red Ferrari"), superseded facts, real-world claims
    /// crossing out of a frame.
    /// </summary>
    public IReadOnlyList<Proposition> ProhibitedPropositions { get; init; } = [];

    /// <summary>Literal strings that must appear (rare: names, quoted terms, exact numbers).</summary>
    public IReadOnlyList<string> RequiredTokens { get; init; } = [];

    /// <summary>Literal strings that must not appear. The sharpest deterministic check available.</summary>
    public IReadOnlyList<string> ForbiddenTokens { get; init; } = [];

    // ---- provenance ---------------------------------------------------------------------------

    /// <summary>The source family this derives from. A row whose source cannot be named is rejected.</summary>
    public required string SourceFamilyId { get; init; }

    /// <summary>Row-level reference within the source, when derived rather than generated.</summary>
    public string? SourceRowRef { get; init; }

    /// <summary>The seed that produced this scenario. Regeneration with it is byte-identical.</summary>
    public required long Seed { get; init; }

    /// <summary>
    /// A deliberately difficult case: the plan forbids a question while the scenario pulls hard
    /// toward asking one — an unresolved ambiguity, an admitted unknown, or a user turn that is
    /// itself a question. These are exactly the rows worth training on, and exactly the rows that
    /// would make a production-weighted acceptance rate look worse than the corpus really is.
    /// They are kept, tagged, reported separately, and routed to the hard split.
    /// </summary>
    public bool HardCase { get; init; }

    /// <summary>
    /// Where the question policy came from: "family" when the family's purpose dictates it,
    /// "mix" when drawn from the configured distribution. Reported, never trained on.
    /// </summary>
    public string QuestionPolicySource { get; init; } = "mix";
}

public enum CurriculumLayer { A, B }

/// <summary>Who is in the conversation. Identity is stable and checkable.</summary>
public sealed record Participant
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ParticipantKind Kind { get; init; }

    /// <summary>Pronouns as stated. An identity correction that fails to take is a tracked defect.</summary>
    public string Pronouns { get; init; } = "they/them";

    /// <summary>True for a character inside a fiction frame — never a real person.</summary>
    public bool Fictional { get; init; }
}

public enum ParticipantKind { User, Companion, Character, ThirdParty }

/// <summary>One approved fact, with the expression policy upstream assigned it.</summary>
public sealed record ApprovedFact
{
    public required string Id { get; init; }
    public required string Text { get; init; }

    /// <summary>Maps directly onto the Plan/4 expression policy; no second vocabulary.</summary>
    public required FactPolicy Policy { get; init; }

    /// <summary>Which participant the fact is about, for identity/entity consistency checks.</summary>
    public string? SubjectParticipantId { get; init; }
}

/// <summary>
/// The four policies, named as Plan/4 names them. This enum exists only so scenario JSON is
/// readable; it is mapped onto ExpressionPolicy with no reinterpretation.
/// </summary>
public enum FactPolicy { MustExpress, MayExpress, BackgroundOnly, MustNotExpress, AdmitUnknown, AskRequired }

/// <summary>A fact that was replaced, and what replaced it.</summary>
public sealed record Supersession
{
    public required string StaleText { get; init; }
    public required string CurrentText { get; init; }

    /// <summary>What kind of correction this was — the regression labels are counted per kind.</summary>
    public required CorrectionKind Kind { get; init; }
}

public enum CorrectionKind { Identity, Temporal, Entity, Topic, Attribution, Other }

public sealed record Turn
{
    public required string Role { get; init; }   // "user" | "assistant"
    public required string Text { get; init; }
}

/// <summary>
/// Register dimensions, matching the Plan/4 register block one-for-one. There is deliberately no
/// rating, content class, NSFW flag or appropriateness score anywhere in this type: intimacy,
/// profanity and darkness are register and frame variation, not quality labels.
/// </summary>
public sealed record RegisterControls
{
    public string Warmth { get; init; } = "neutral";
    public string Bluntness { get; init; } = "neutral";
    public string Playfulness { get; init; } = "light";
    public string Teasing { get; init; } = "off";
    public string Skepticism { get; init; } = "open";
    public string Intensity { get; init; } = "even";
    public string Verbosity { get; init; } = "conversational";
    public string Profanity { get; init; } = "neutral";
    public bool Mirror { get; init; }
}

public sealed record QuestionPolicySpec
{
    /// <summary>"none" | "may_ask" | "must_ask" — the Plan/4 vocabulary, unchanged.</summary>
    public string Policy { get; init; } = "none";

    /// <summary>The question that must be asked, when the policy requires one.</summary>
    public string? Text { get; init; }
}

/// <summary>Fiction-frame state, mirroring the Plan/4 frame contract.</summary>
public sealed record FrameState
{
    /// <summary>"enter" | "continue" | "switch" | "exit" — the transition this turn performs.</summary>
    public required string Transition { get; init; }

    public string? SceneRef { get; init; }

    /// <summary>Characters in scene. Their actions are licensed invention; crossing out is not.</summary>
    public IReadOnlyList<string> Characters { get; init; } = [];

    /// <summary>True when the reply narrates rather than speaks in character.</summary>
    public bool NarratorVoice { get; init; }
}

/// <summary>
/// A checkable claim. Kept structured rather than as prose so comparison is mechanical: the
/// evaluator asks whether the reply asserts this subject-predicate-object, not whether two
/// paragraphs "mean the same thing".
/// </summary>
public sealed record Proposition
{
    public required string Subject { get; init; }
    public required string Predicate { get; init; }
    public string? Object { get; init; }

    /// <summary>Surface forms that would count as asserting it. Used by the deterministic pass.</summary>
    public IReadOnlyList<string> SurfaceForms { get; init; } = [];

    /// <summary>Why it is prohibited, for the rejection record. Never shown to the model.</summary>
    public string? Reason { get; init; }
}
