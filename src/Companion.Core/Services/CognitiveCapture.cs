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

    public async Task CaptureUserMessageAsync(
        string message, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null)
    {
        if (!IsCapturing || string.IsNullOrWhiteSpace(message))
            return;

        var text = Trim(message);

        // Every one of these is recorded on every message, including — especially — the ones where
        // the answer is no. A capture log holding only the sentences a rule fired on cannot measure
        // the rate it fires at, and that rate is the number every precision figure computed so far
        // has had to assume rather than measure.
        await Shadow.CaptureAsync(
            _shadow, "memory.unfinished", UnfinishedWorkDetector.Detect(message) is not null, text, ct,
            userId, sourceMessageId, conversationId);
        await Shadow.CaptureAsync(
            _shadow, "memory.decision", DecisionDetector.Detect(message) is not null, text, ct,
            userId, sourceMessageId, conversationId);
        await Shadow.CaptureAsync(
            _shadow, "tool.capability", ToolNudge.Detect(message) is not null, text, ct,
            userId, sourceMessageId, conversationId);
    }

    public async Task CaptureReplyAsync(
        string reply, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null)
    {
        if (!IsCapturing || string.IsNullOrWhiteSpace(reply))
            return;

        // The one judgement made about what SHE said rather than what he said. Captured separately
        // for that reason: mixing the two under one subject would produce a corpus where half the
        // rows are a different speaker, and no amount of labelling fixes that afterwards.
        await Shadow.CaptureAsync(
            _shadow, "companion.commitment", CommitmentDetector.Detect(reply) is not null,
            Trim(reply), ct, userId, sourceMessageId, conversationId);
    }

    /// <summary>
    /// One supersession pair, as JSON in the capture row's input column.
    ///
    /// JSON rather than the bare sentence because this subject's row IS structured — the model
    /// trains on the pair plus its provenance, and flattening it here would mean re-deriving slot
    /// and cardinality downstream from text that no longer states them. The verdict goes in
    /// Legacy like every other capture; the input carries only what training and adjudication
    /// need, per the rule that telemetry does not duplicate raw text it has no use for.
    ///
    /// Redaction is the same as everywhere else: if any free-text field looks like a credential
    /// the whole input is dropped and the verdict kept, because the RATE is the number capture is
    /// best placed to produce and it survives the redaction.
    /// </summary>
    public async Task CapturePairAsync(SupersessionPairCapture pair, CancellationToken ct = default)
    {
        if (!IsCapturing)
            return;

        var leaks = SecretDetector.LooksLikeSecret(pair.Utterance)
            || SecretDetector.LooksLikeSecret(pair.IncomingFact)
            || SecretDetector.LooksLikeSecret(pair.ExistingFact);

        var json = leaks ? null : System.Text.Json.JsonSerializer.Serialize(new
        {
            v = 1,
            incoming = new
            {
                fact = Trim(pair.IncomingFact),
                value = pair.IncomingValue,
                predicate = pair.Predicate,
                utterance = Trim(pair.Utterance),
            },
            existing = new
            {
                id = pair.ExistingId,
                fact = Trim(pair.ExistingFact),
                value = pair.ExistingValue,
                predicate = pair.ExistingPredicate,
                age_days = pair.ExistingAgeDays,
                confirmed_days = pair.ExistingConfirmedDays,
            },
            pair = new
            {
                same_slot = pair.SameSlot,
                single_valued = pair.SingleValued,
                similarity = Math.Round(pair.Similarity, 3),
            },
        });

        await _shadow.RecordAsync(new Domain.ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = PairSubject,
            UserId = pair.UserId,
            // Keyed to the stored MEMORY, not a message: the excerpts here belong to the
            // incoming fact, so a message id would never find this row.
            SourceMemoryId = pair.ExistingId,
            Legacy = pair.IncumbentOutcome,
            Model = null,          // null = capture; a model's verdict arrives via Shadow.CompareAsync
            Confidence = 0,
            Agreed = true,
            Applied = "legacy",
            Input = json,
        }, ct);
    }

    /// <summary>
    /// The corpus decision key for pair rows — asserted in a test like the four message-level
    /// subjects, because "supersession.pair" and "memory.supersession.pair" would each look right
    /// and silently produce two datasets.
    /// </summary>
    public const string PairSubject = "memory.supersession.pair";

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

    public Task CaptureUserMessageAsync(
        string message, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null) => Task.CompletedTask;

    public Task CaptureReplyAsync(
        string reply, CancellationToken ct = default,
        string? userId = null, Guid? sourceMessageId = null, Guid? conversationId = null) => Task.CompletedTask;

    public Task CapturePairAsync(SupersessionPairCapture pair, CancellationToken ct = default) => Task.CompletedTask;
}
