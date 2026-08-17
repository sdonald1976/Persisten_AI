using System.Diagnostics;
using Companion.Core.Domain;

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

    private static string Describe<T>(T value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };
}
