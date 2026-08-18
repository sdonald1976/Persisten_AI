using Companion.Core.Abstractions;

namespace Companion.Core.Services;

/// <summary>
/// Records what the classifier-shaped heuristics said about real sentences, so that the corpus a
/// specialist model is judged on eventually stops being one person's guesses about English.
///
/// Every model verdict in <c>docs/SPECIALIST_MODELS.md</c> so far ends at the same sentence: the
/// corpus is synthetic, the same person wrote the templates and the rule they are testing, and
/// nobody can tell whether a model generalised or learned that person's habits. The measured
/// numbers are worse than that suggests — `memory.unfinished` gets a fold-to-fold spread of ±0.27
/// on forty template families, which is wider than every difference being measured. More families
/// written by the same hand does not fix either problem. Real sentences do, and they arrive
/// through the turn.
///
/// Two rules define what this is allowed to be:
///
/// It never changes a decision. It runs after the reply is generated and stored, reads nothing the
/// turn depends on, and its failures are swallowed by the recorder. A measurement that can cost a
/// conversation has stopped being worth taking.
///
/// It writes nothing a turn was not already allowed to remember. The caller runs it under the same
/// gate as memory extraction — not a private conversation, not an in-character one, extraction
/// enabled — because a sentence the user asked her to forget is not training data either. On top
/// of that, <see cref="Shadow.CaptureAsync"/> drops the text (keeping the verdict) when it looks
/// like a credential.
///
/// The subjects are deliberately the same strings <c>CognitiveCorpus</c> uses for its generated
/// decisions. A captured row and a generated row are then the same row about the same judgement,
/// which is the only reason the two can ever be trained on together.
/// </summary>
public sealed class CognitiveCapture : ICognitiveCapture
{
    /// <summary>
    /// Long messages are truncated rather than dropped. A judgement made on a whole paragraph is
    /// still a judgement worth reviewing, and an unbounded column in a table kept for weeks is how
    /// telemetry quietly becomes a second copy of the conversation.
    /// </summary>
    private const int MaxCapturedChars = 600;

    private readonly IShadowRecorder _shadow;

    public CognitiveCapture(IShadowRecorder shadow) => _shadow = shadow;

    public bool IsCapturing => _shadow.IsRecording;

    public async Task CaptureUserMessageAsync(string message, CancellationToken ct = default)
    {
        if (!IsCapturing || string.IsNullOrWhiteSpace(message))
            return;

        var text = Trim(message);

        // Every one of these is recorded on every message, including — especially — the ones where
        // the answer is no. A capture log holding only the sentences a rule fired on cannot measure
        // the rate it fires at, and that rate is the number every precision figure computed so far
        // has had to assume rather than measure.
        await Shadow.CaptureAsync(_shadow, "memory.unfinished", UnfinishedWorkDetector.Detect(message) is not null, text, ct);
        await Shadow.CaptureAsync(_shadow, "memory.decision", DecisionDetector.Detect(message) is not null, text, ct);
        await Shadow.CaptureAsync(_shadow, "tool.capability", ToolNudge.Detect(message) is not null, text, ct);
    }

    public async Task CaptureReplyAsync(string reply, CancellationToken ct = default)
    {
        if (!IsCapturing || string.IsNullOrWhiteSpace(reply))
            return;

        // The one judgement made about what SHE said rather than what he said. Captured separately
        // for that reason: mixing the two under one subject would produce a corpus where half the
        // rows are a different speaker, and no amount of labelling fixes that afterwards.
        await Shadow.CaptureAsync(
            _shadow, "companion.commitment", CommitmentDetector.Detect(reply) is not null, Trim(reply), ct);
    }

    private static string Trim(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxCapturedChars ? trimmed : trimmed[..MaxCapturedChars];
    }
}

/// <summary>The capture that isn't happening, so the turn never has to check a null.</summary>
public sealed class NoCognitiveCapture : ICognitiveCapture
{
    public bool IsCapturing => false;

    public Task CaptureUserMessageAsync(string message, CancellationToken ct = default) => Task.CompletedTask;

    public Task CaptureReplyAsync(string reply, CancellationToken ct = default) => Task.CompletedTask;
}
