namespace Companion.Core.Domain;

/// <summary>One intent the classifier considered, with why and how strongly.</summary>
public sealed record IntentCandidate(string Intent, double Confidence, string Reason);

/// <summary>
/// SHADOW relevance feature (being validated, not yet consumed by anything): whether any
/// retrieved memory actually contains the user message's focal terms. Max raw topical score
/// failed to separate known from unknown in the 2026-08-20 evidence run (question scaffolding
/// contaminates overlap: the carburetor scored 1.95 but the unknown treehouse still hit 1.49);
/// containment of the question's subject nouns separated every case in that run and is being
/// characterized against a broader corpus before admit-unknown may threshold on it.
/// </summary>
/// <param name="FocalTerms">The message's subject words after scaffolding is stripped.</param>
/// <param name="Covered">True when at least one focal term appears in a retrieved memory.</param>
/// <param name="CoveredBy">The covering memory's text (bounded), for the trace.</param>
public sealed record FocalCoverage(IReadOnlyList<string> FocalTerms, bool Covered, string? CoveredBy);

/// <summary>
/// What Ava should DO this turn — not what to say, not how to sound. Personality stays
/// downstream; this describes the act (answer, acknowledge, clarify…), never the prose.
///
/// SHADOW STATE (language-organ Phase 2): computed every turn, recorded on the diagnostics
/// ring and captured for corpus review, and deliberately NOT given to the generation prompt.
/// It earns authority only if the captured data shows the classifications are useful — the
/// same bar every other heuristic in this codebase has to clear.
/// </summary>
public sealed record TurnIntentState
{
    /// <summary>The selected intent, from the closed vocabulary in
    /// Services.TurnIntentClassifier.Intents — or "unknown" when nothing clears the
    /// confidence bar. Unknown means "continue naturally", and it is the preferred answer
    /// over a confidently wrong one.</summary>
    public required string Intent { get; init; }

    /// <summary>The selection's heuristic strength (0..1). Below the classifier's bar the
    /// selected intent is "unknown" and this is the best rejected candidate's score.</summary>
    public double Confidence { get; init; }

    /// <summary>Why this intent, in one sentence.</summary>
    public string? Reason { get; init; }

    /// <summary>Every intent that matched, strongest first — including the winner. The
    /// competing candidates are the interesting part of a shadow: a close second on many
    /// turns is the vocabulary telling us where it is confused.</summary>
    public IReadOnlyList<IntentCandidate> Candidates { get; init; } = Array.Empty<IntentCandidate>();
}
