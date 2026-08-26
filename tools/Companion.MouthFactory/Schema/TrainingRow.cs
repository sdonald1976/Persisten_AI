using System.Text.Json.Serialization;

namespace Companion.MouthFactory.Schema;

/// <summary>
/// A training row: exactly what the model sees, and nothing else.
///
/// Three fields reach training — <see cref="System"/>, <see cref="Input"/>, <see cref="Target"/>
/// — and they are produced by <c>MouthPromptV4</c>, the same production function the shipping
/// renderer will call. Everything the factory knows about the row lives in
/// <see cref="TrainingRowMetadata"/>, in a separate file, so there is no path by which a critic's
/// rationale, a rejection reason, a scenario's hidden state or a seed can end up in the target.
///
/// The separation is structural rather than procedural on purpose: a metadata field on this
/// record would eventually be exported by someone in a hurry.
/// </summary>
public sealed record TrainingRow
{
    public const string SchemaVersion = "training-row/1.0";

    [JsonPropertyName("schemaVersion")] public string Version { get; init; } = SchemaVersion;

    /// <summary>Stable id, shared with the metadata record and the ledger.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>The system message: the rendered context packet, byte for byte.</summary>
    [JsonPropertyName("system")] public required string System { get; init; }

    /// <summary>The user message: CompactV4 plan, transcript window, the turn being answered.</summary>
    [JsonPropertyName("input")] public required string Input { get; init; }

    /// <summary>The utterance. Nothing else. No rationale, no scores, no labels.</summary>
    [JsonPropertyName("target")] public required string Target { get; init; }

    /// <summary>Which format produced Input. A row from a different version never mixes in.</summary>
    [JsonPropertyName("formatVersion")] public required string FormatVersion { get; init; }
}

/// <summary>
/// Everything about a row that must never be shown to the model. Written to a sibling file keyed
/// by row id.
/// </summary>
public sealed record TrainingRowMetadata
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioFamilyId { get; init; }
    public required string FamilyId { get; init; }
    public required CurriculumLayer Layer { get; init; }
    public required string SourceFamilyId { get; init; }
    public string? SourceRowRef { get; init; }

    /// <summary>Which of several valid targets for this plan this row is.</summary>
    public int VariantIndex { get; init; }

    public required GenerationProvenance Generation { get; init; }

    /// <summary>Every deterministic check and critic verdict. Diagnostics only.</summary>
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    /// <summary>Transcript turns, for context-bucket reporting.</summary>
    public int TranscriptTurns { get; init; }

    public int TargetWords { get; init; }

    /// <summary>First few words, lowercased — feeds opening-diversity analysis.</summary>
    public string Opening { get; init; } = "";

    /// <summary>train | validation | test | hard | unseen. Assigned by the family-aware splitter.</summary>
    public string? Split { get; init; }
}

/// <summary>Exactly how a candidate came to exist, so it can be reproduced or blamed.</summary>
public sealed record GenerationProvenance
{
    public required string Role { get; init; }
    public required string Model { get; init; }
    public required string Endpoint { get; init; }
    public required string PromptVersion { get; init; }
    public required long Seed { get; init; }
    public double Temperature { get; init; }
    public int Attempt { get; init; }

    /// <summary>Hash of the exact prompt sent. Never the prompt itself — it can carry scenario text.</summary>
    public required string PromptHash { get; init; }
}

/// <summary>One check's verdict. Deterministic checks and critics share the shape.</summary>
public sealed record CheckResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }

    /// <summary>Machine-readable reason code. Never free-form critic prose.</summary>
    public string? Code { get; init; }

    /// <summary>Diagnostic detail. Stored here and nowhere near the target.</summary>
    public string? Detail { get; init; }

    /// <summary>Critic score where one applies; null for boolean checks.</summary>
    public double? Score { get; init; }

    public required CheckKind Kind { get; init; }
}

/// <summary>
/// Deterministic checks run first and can reject alone. Critics run last and, by themselves,
/// only route to manual review — a model opinion never silently discards a structurally valid row.
/// </summary>
public enum CheckKind { Deterministic, Critic }

/// <summary>Where a candidate ended up.</summary>
public enum Disposition { Accepted, Rejected, ManualReview }
