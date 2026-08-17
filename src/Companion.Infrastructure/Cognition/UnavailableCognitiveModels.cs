using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// What every specialist model is until someone configures one.
///
/// These are not stubs waiting to be replaced by the real thing — they are the permanent answer to
/// "no model is configured", which is the default and expected state. Every caller has a heuristic
/// to fall back to, so the honest response to a missing model is to say so and get out of the way,
/// not to throw or to invent a score. That is why they report <see cref="ICognitiveModel.IsAvailable"/>
/// as false and return nothing rather than something neutral-looking: a zero score is a judgment,
/// and these have not made one.
/// </summary>
internal static class Unavailable
{
    public static CognitiveModelStatus StatusFor(string name, string reason)
        => new() { Name = name, Available = false, Reason = reason };
}

internal sealed class UnavailableTextPairScorer : ITextPairScorer
{
    private readonly string _reason;

    public UnavailableTextPairScorer(string name, string reason)
    {
        Name = name;
        _reason = reason;
    }

    public string Name { get; }
    public bool IsAvailable => false;
    public CognitiveModelStatus Status => Unavailable.StatusFor(Name, _reason);

    public Task<IReadOnlyList<double>> ScoreAsync(
        string query, IReadOnlyList<string> candidates, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<double>>(Array.Empty<double>());
}

internal sealed class UnavailableNliModel : INliModel
{
    private readonly string _reason;

    public UnavailableNliModel(string name, string reason)
    {
        Name = name;
        _reason = reason;
    }

    public string Name { get; }
    public bool IsAvailable => false;
    public CognitiveModelStatus Status => Unavailable.StatusFor(Name, _reason);

    // Neutral with zero confidence, which callers must read as "no opinion" rather than as
    // "these facts are unrelated" — the difference decides whether a memory survives.
    public Task<EntailmentVerdict> ClassifyAsync(string premise, string hypothesis, CancellationToken ct = default)
        => Task.FromResult(EntailmentVerdict.Unknown);
}

internal sealed class UnavailableTextClassifier : ITextClassifier
{
    private readonly string _reason;

    public UnavailableTextClassifier(string name, string reason)
    {
        Name = name;
        _reason = reason;
    }

    public string Name { get; }
    public bool IsAvailable => false;
    public CognitiveModelStatus Status => Unavailable.StatusFor(Name, _reason);

    public Task<IReadOnlyList<SignalScore>> ClassifyAsync(string text, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SignalScore>>(Array.Empty<SignalScore>());
}
