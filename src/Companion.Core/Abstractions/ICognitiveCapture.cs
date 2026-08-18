namespace Companion.Core.Abstractions;

/// <summary>
/// Records the heuristics' verdicts on real sentences, for the corpus a future model is judged on.
///
/// Separate from <see cref="IShadowRecorder"/> because they answer different questions and are
/// switched on for different reasons: shadow mode asks "does this model agree with the rule it
/// would replace", and needs a model; capture asks "what does the rule actually do out here", and
/// needs only the rule. Capture is what you turn on when there is no model yet, which — for the
/// cognitive classifier — is today.
/// </summary>
public interface ICognitiveCapture
{
    /// <summary>Whether anything is being recorded, so callers can skip the work entirely.</summary>
    bool IsCapturing { get; }

    /// <summary>Records the classifier-shaped judgements made about one user message.</summary>
    Task CaptureUserMessageAsync(string message, CancellationToken ct = default);

    /// <summary>Records the judgements made about the companion's own reply.</summary>
    Task CaptureReplyAsync(string reply, CancellationToken ct = default);
}
