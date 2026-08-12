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

    public bool HasMusing => !string.IsNullOrWhiteSpace(Musing);
}
