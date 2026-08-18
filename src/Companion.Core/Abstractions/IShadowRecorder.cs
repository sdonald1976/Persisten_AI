using System.Diagnostics;
using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.Core.Abstractions;

/// <summary>
/// Records what a specialist model would have decided, next to what the heuristic actually decided.
///
/// The migration this exists for is deliberately slow: heuristic, then model beside it, then
/// measurement, then promotion, then removal. Shadow mode is the middle of that, and the reason it
/// comes before the first real model rather than after several is that "a model replaces a
/// heuristic when we have evidence it performs better" is only a rule if the evidence exists first.
///
/// Recording must never affect a turn. A failure here is a log line — telemetry that can break a
/// conversation is worse than no telemetry, and the same rule already governs
/// <see cref="IDiagnosticsStore"/>.
/// </summary>
public interface IShadowRecorder
{
    /// <summary>
    /// Whether anything is being recorded. Callers check this before doing the extra work of
    /// running a model they are going to throw away — shadow mode costs a real inference per turn,
    /// and when it is off that cost should not be paid at all.
    /// </summary>
    bool IsRecording { get; }

    Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default);

    /// <summary>Agreement rates per subject since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
        DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The most recent disagreements, newest first — the queue of things worth a human look.</summary>
    Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
        string? subject, int count, CancellationToken ct = default);

    /// <summary>
    /// The most recent captures — rows with no model answer, newest first. See
    /// <see cref="Shadow.CaptureAsync"/> for why these exist and what they are for.
    /// </summary>
    Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
        string? subject, int count, CancellationToken ct = default);
}

/// <summary>
/// Runs a heuristic and a model over the same question and records the pair, returning the answer
/// the caller should actually use.
///
/// A helper rather than a pattern each caller reimplements, because the part that is easy to get
/// wrong is not the recording — it is remembering that while shadowing, the value returned must be
/// the heuristic's. A shadow comparison that quietly changes behaviour is not a shadow.
/// </summary>
public static class Shadow
{
    /// <summary>
    /// Evaluates <paramref name="model"/> alongside <paramref name="legacy"/> and returns the
    /// legacy answer, recording both. When recording is off, or the model has no opinion, the
    /// model is not run at all and the legacy answer is returned untouched.
    /// </summary>
    public static async Task<T> CompareAsync<T>(
        IShadowRecorder recorder,
        string subject,
        T legacy,
        Func<CancellationToken, Task<(T Value, double Confidence)?>> model,
        string? input = null,
        CancellationToken ct = default)
    {
        if (!recorder.IsRecording)
            return legacy;

        var started = Stopwatch.GetTimestamp();
        (T Value, double Confidence)? judged;
        try
        {
            judged = await model(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A model that throws in shadow has told us something useful and must not cost a turn.
            judged = null;
        }

        if (judged is null)
            return legacy;

        var elapsed = Stopwatch.GetElapsedTime(started);
        var legacyText = Describe(legacy);
        var modelText = Describe(judged.Value.Value);

        await recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            Legacy = legacyText,
            Model = modelText,
            Confidence = judged.Value.Confidence,
            Agreed = string.Equals(legacyText, modelText, StringComparison.Ordinal),
            Applied = "legacy",
            DurationMs = (long)elapsed.TotalMilliseconds,
            Input = input,
        }, ct);

        return legacy;
    }

    /// <summary>
    /// Records what the heuristic said about a real sentence, with no model involved at all.
    ///
    /// This is the way out of a deadlock the rest of this document keeps arriving at. Every "what
    /// would change the verdict" note in <c>docs/SPECIALIST_MODELS.md</c> ends at the same place:
    /// the corpus is synthetic, one person wrote every template, and the honest answer to "does the
    /// model generalise or has it learned that person's writing habits" is that nobody can tell. A
    /// model needs real examples; real examples arrive through the turn; and shadow comparison
    /// cannot collect them because it needs a model to compare against.
    ///
    /// So this records the half that already exists. The incumbent's verdict on a real sentence is
    /// a weak label, and a weak label plus the sentence is exactly what a human review queue is
    /// made of. When a classifier does arrive it fills in <see cref="ShadowComparison.Model"/> on
    /// the same subject and these rows become ordinary comparisons — the subject name is
    /// deliberately the corpus decision key, so a captured row and a generated row are the same
    /// shape and can be trained on together.
    ///
    /// <paramref name="input"/> is dropped, and only the verdict kept, when the text looks like it
    /// contains a credential. That is a deliberate asymmetry rather than skipping the row: the
    /// RATE the heuristic fires at is the thing this corpus most badly needs — every precision
    /// figure computed so far has had to assume a conversational base rate rather than measure one
    /// — and the rate is knowable without keeping the sentence.
    /// </summary>
    public static async Task CaptureAsync(
        IShadowRecorder recorder,
        string subject,
        bool legacy,
        string? input,
        CancellationToken ct = default)
    {
        if (!recorder.IsRecording)
            return;

        await recorder.RecordAsync(new ShadowComparison
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            Legacy = legacy ? "true" : "false",

            // Null is what makes this a capture rather than a comparison, and every reader keys off
            // it: agreement rates exclude these rows, because a model that was never asked cannot
            // have agreed with anything.
            Model = null,
            Confidence = 0,
            Agreed = true,
            Applied = "legacy",
            Input = SecretDetector.LooksLikeSecret(input) ? null : input,
        }, ct);
    }

    private static string Describe<T>(T value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };
}
