namespace Companion.Core.Domain;

/// <summary>
/// One entry in the companion's private diary — a thought it had <em>between</em> conversations,
/// produced by the reflection pass while the user was away. A musing is the companion's own
/// interior voice ("she's mentioned the project partner twice now; I still don't know their name"), never a
/// fact about the user: it is surfaced to the model under an explicit "your own thought — hold
/// loosely" label and must never be laundered into semantic memory or asserted back as something
/// the user said.
///
/// A reflection also carries the watermark (<see cref="CoveredThrough"/>) that makes the pass
/// idempotent: the next pass only reads messages newer than the last one's watermark. A quiet
/// stretch produces a watermark-only entry with no musing (<see cref="HasMusing"/> is false) —
/// the pass happened, there was just nothing worth writing down.
/// </summary>
public sealed class Reflection
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The diary entry itself, in the companion's voice. Null on a quiet day.</summary>
    public string? Musing { get; set; }

    /// <summary>Timestamp of the newest message this pass covered — the next pass starts after it.</summary>
    public DateTimeOffset CoveredThrough { get; set; }

    /// <summary>How many new messages the pass read (bookkeeping/explainability).</summary>
    public int MessagesReflected { get; set; }

    /// <summary>Embedding of <see cref="Musing"/>, so a past thought can be found again. Null when quiet.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// The train of thought this musing belongs to. Reflections used to be independent rows whose
    /// only relationship was a timestamp, so "what has she been working through this week?" had no
    /// answer. A thread id groups the entries where she kept developing the SAME thought across
    /// passes; a genuinely new thought starts its own thread (id = this reflection's id).
    /// </summary>
    public Guid ThreadId { get; set; }

    /// <summary>
    /// The specific earlier musing this one continues, when it continues one. Null for the first
    /// entry in a thread. Always a real reflection of the same user — the model proposes it, but
    /// the id is validated against stored rows before it is trusted.
    /// </summary>
    public Guid? ContinuesReflectionId { get; set; }

    /// <summary>
    /// Set when she decides a thread has reached its end ("I think I've worked that out") — so a
    /// resolved train of thought stops being resumed and can't turn into rumination.
    /// </summary>
    public bool ThreadSettled { get; set; }

    public bool HasMusing => !string.IsNullOrWhiteSpace(Musing);
}

/// <summary>
/// A train of thought reconstructed from the diary: the entries of one thread in order, with
/// where it currently stands. Derived on read — the reflections themselves stay the source of truth.
/// </summary>
public sealed record ReflectionThread
{
    public required Guid ThreadId { get; init; }
    public required IReadOnlyList<Reflection> Entries { get; init; }

    /// <summary>The thought as it currently stands — the newest entry in the thread.</summary>
    public Reflection Latest => Entries[^1];

    public DateTimeOffset StartedAt => Entries[0].CreatedAt;
    public DateTimeOffset LastDevelopedAt => Latest.CreatedAt;

    /// <summary>True once she has decided the thread is worked out.</summary>
    public bool Settled => Latest.ThreadSettled;

    /// <summary>How many passes she has returned to this thought.</summary>
    public int Length => Entries.Count;
}
