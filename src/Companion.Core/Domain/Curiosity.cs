namespace Companion.Core.Domain;

/// <summary>
/// Something the companion genuinely wants to know — a question minted by the reflection pass
/// ("she mentioned her sister twice but never her name"). A curiosity is voiced at most once:
/// surfacing it (as a greeting opener or woven into a reply) marks it <see cref="CuriosityStatus.Voiced"/>
/// immediately, so the companion raises a thing one time and then lets it go — curiosity, never
/// interrogation. The user answering is not required to close it; asking once is the budget.
/// </summary>
public sealed class Curiosity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>The reflection pass this question arose from (provenance).</summary>
    public Guid ReflectionId { get; set; }

    /// <summary>The question itself, phrased as the companion would ask it.</summary>
    public string Question { get; set; } = default!;

    /// <summary>What it's about — a short topic phrase ("her sister", "the interview"). Used for dedupe.</summary>
    public string? About { get; set; }

    /// <summary>Why the companion wonders this (kept for explainability, never shown verbatim).</summary>
    public string? Reason { get; set; }

    public CuriosityStatus Status { get; set; } = CuriosityStatus.Open;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was surfaced to the user (set the moment it is injected, not when answered).</summary>
    public DateTimeOffset? VoicedAt { get; set; }
}

public enum CuriosityStatus
{
    /// <summary>Held, not yet raised.</summary>
    Open,

    /// <summary>Raised once (greeting opener or in-reply) — it will not be raised again.</summary>
    Voiced,

    /// <summary>Dropped without being raised (e.g. superseded or stale).</summary>
    Dismissed,
}
