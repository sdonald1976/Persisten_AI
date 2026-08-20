using System.Text.Json.Serialization;

namespace Companion.Core.Domain;

/// <summary>Rough category of a concept. Persons are deliberately absent — third-party
/// facts are SubjectGuard's settled territory (see docs/CONCEPT_KNOWLEDGE.md §8).</summary>
[JsonConverter(typeof(KebabEnumConverter<ConceptKind>))]
public enum ConceptKind { Object, Place, Idea, Process, Organism, Other }

/// <summary>
/// Where a piece of Ava-owned knowledge came from. There is deliberately NO value for
/// "the chat model said so": pretrained knowledge is not low-confidence Ava-knowledge, it
/// is unrepresentable in the store — the PredicateVocabulary move applied to epistemics.
/// </summary>
[JsonConverter(typeof(KebabEnumConverter<KnowledgeOrigin>))]
public enum KnowledgeOrigin
{
    /// <summary>The user taught it in conversation; evidence cites their message verbatim.</summary>
    Taught,

    /// <summary>A document/book taught it (future producer; nothing writes this yet).</summary>
    Document,

    /// <summary>A trusted tool supplied it (future; nothing writes this yet).</summary>
    ToolVerified,

    /// <summary>Inferred from other assertions (future; nothing writes this yet).</summary>
    Derived,
}

/// <summary>The closed relation vocabulary — one typed relation per assertion, following
/// the PredicateVocabulary lesson: an open relation space is the root cause waiting to
/// happen. Strings appear only as the OBJECT of a typed relation.</summary>
[JsonConverter(typeof(KebabEnumConverter<ConceptRelation>))]
public enum ConceptRelation
{
    /// <summary>The definitional gloss. SINGLE-VALUED: re-teaching supersedes with history.</summary>
    DefinedAs,

    IsA,
    UsedFor,
    HasProperty,
    PartOf,
    HasPart,

    /// <summary>The low-authority escape hatch — never rendered as a claim. If this fills
    /// up, THAT is the evidence a richer structure must present before being built.</summary>
    AssociatedWith,
}

/// <summary>Ava's epistemic state toward a concept — the typed answer to "does she know X?".</summary>
[JsonConverter(typeof(KebabEnumConverter<ConceptFamiliarity>))]
public enum ConceptFamiliarity
{
    /// <summary>No concept of this name or alias exists. She has not learned it.</summary>
    Unknown,

    /// <summary>The concept exists but carries no active assertions — named, never taught.</summary>
    Heard,

    /// <summary>Only candidate/low-confidence assertions exist.</summary>
    Learning,

    /// <summary>Active assertions exist; she has learned this, and can say from where.</summary>
    Known,

    /// <summary>Its defining assertion is disputed.</summary>
    Disputed,
}

/// <summary>A concept's identity: the thing itself, apart from anything believed about it.</summary>
public class Concept
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>Normalized (trimmed, lower-cased) name used for exact lookup and dedup.</summary>
    public string CanonicalName { get; set; } = default!;

    /// <summary>The name as first taught, for rendering.</summary>
    public string DisplayName { get; set; } = default!;

    public ConceptKind Kind { get; set; } = ConceptKind.Other;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An alternative name, created only by explicit equivalence in a teaching
/// statement — never inferred.</summary>
public class ConceptAlias
{
    public Guid Id { get; set; }
    public Guid ConceptId { get; set; }
    public string UserId { get; set; } = default!;
    public string Alias { get; set; } = default!;
}

/// <summary>
/// One belief Ava holds about a concept: a typed relation whose object is either another
/// concept (a typed edge) or a bounded language payload. Implements <see cref="IMemory"/>
/// so it flows through retrieval, the vector index, and context assembly as
/// <see cref="MemoryKind.Concept"/> — and it is the first honest writer of
/// <see cref="MemoryOwner.Companion"/>: this is HER knowledge, not a fact about the user.
/// Evidence rows (MemoryEvidence, kind=Concept) are required exactly as for memories, and
/// may only ever cite USER-authored messages — the structural barrier that keeps the chat
/// model's own words from laundering into her knowledge (docs/CONCEPT_KNOWLEDGE.md §5).
/// </summary>
public class ConceptAssertion : IMemory
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid ConceptId { get; set; }

    public ConceptRelation Relation { get; set; }

    /// <summary>Set when the object is another concept.</summary>
    public Guid? TargetConceptId { get; set; }

    /// <summary>Set when the object is language (a definition, a property).</summary>
    public string? Value { get; set; }

    /// <summary>The retrievable sentence ("An axe is a tool with…"); embedded.</summary>
    public string NormalizedText { get; set; } = default!;

    public KnowledgeOrigin Origin { get; set; } = KnowledgeOrigin.Taught;

    public double Confidence { get; set; }
    public double Importance { get; set; } = 0.6;
    public Validity Validity { get; set; } = Validity.Current;
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;
    public Guid? SupersededById { get; set; }

    public DateTimeOffset FirstObserved { get; set; }
    public DateTimeOffset LastConfirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public float[]? Embedding { get; set; }

    /// <summary>Populated at write time; persisted separately (same pattern as memories).</summary>
    public List<MemoryEvidence> Evidence { get; } = new();

    // ---- IMemory ----
    public MemoryKind Kind => MemoryKind.Concept;
    public MemoryOwner Owner => MemoryOwner.Companion;
    public string Content => NormalizedText;
    public DateTimeOffset EffectiveAt => LastConfirmed;
    public string? RelatedProject => null;
}

/// <summary>The typed result of "does Ava know X?" — familiarity plus, when known, the
/// defining assertion and its provenance.</summary>
public sealed record ConceptLookupResult(
    [property: JsonConverter(typeof(KebabEnumConverter<ConceptFamiliarity>))]
    ConceptFamiliarity Familiarity,
    string Term,
    string? Definition = null,
    DateTimeOffset? LearnedAt = null,
    [property: JsonConverter(typeof(KebabEnumConverter<KnowledgeOrigin>))]
    KnowledgeOrigin? Origin = null);
