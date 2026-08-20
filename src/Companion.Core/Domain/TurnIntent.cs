namespace Companion.Core.Domain;

/// <summary>One intent the classifier considered, with why and how strongly.</summary>
public sealed record IntentCandidate(string Intent, double Confidence, string Reason);

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
