namespace Companion.Core.Domain;

/// <summary>
/// Something the companion genuinely wants to know — a question minted by the reflection pass
/// ("she mentioned a project partner twice but never their name"). A curiosity is voiced at most once:
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

    /// <summary>When gap-sourced: the knowledge gap this question pursues (provenance to
    /// the observed epistemic state, beyond the pass that promoted it). Null for
    /// reflection-born curiosities.</summary>
    public Guid? GapId { get; set; }

    /// <summary>The question itself, phrased as the companion would ask it.</summary>
    public string Question { get; set; } = default!;

    /// <summary>What it's about — a short topic phrase ("the project partner", "the interview"). Used for dedupe.</summary>
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

    /// <summary>The conversation answered it (noticed by reflection) — closed with satisfaction.</summary>
    Satisfied,
}
