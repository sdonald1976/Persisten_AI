namespace Companion.Core.Abstractions;

/// <summary>
/// A small specialist model that makes one kind of judgment — is this a decision, do these two
/// facts contradict, how related are these two texts. Distinct from <see cref="IChatModel"/> in
/// every way that matters: it does not generate, it runs in-process on the CPU in milliseconds,
/// and it returns a score rather than prose.
///
/// The contract every implementation shares is that it is <em>optional</em>. A specialist model is
/// an improvement on a heuristic that already works, so the companion has to start, run and pass
/// its tests with none of them present — see <see cref="IsAvailable"/>. Callers ask, and fall back
/// when the answer is no.
/// </summary>
public interface ICognitiveModel
{
    /// <summary>A stable name for configuration, diagnostics and shadow-mode records.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this model is configured, loaded, and able to answer. False when it is switched off,
    /// when its file is missing, or when loading it failed — all of which are ordinary states, not
    /// errors, because the heuristic it would have improved on is still there.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// What is actually loaded, for diagnostics: the file, its version if we know it, and why it
    /// isn't available when it isn't.
    /// </summary>
    CognitiveModelStatus Status { get; }
}

/// <summary>What a specialist model is currently doing, for <c>/diagnostics/models</c>.</summary>
public sealed record CognitiveModelStatus
{
    public required string Name { get; init; }
    public required bool Available { get; init; }

    /// <summary>The resolved model file, when there is one.</summary>
    public string? Path { get; init; }

    /// <summary>Why it isn't available — "disabled", "file not found: …", "failed to load: …".</summary>
    public string? Reason { get; init; }

    /// <summary>How many judgments it has made, and how long they took in total.</summary>
    public long Calls { get; init; }
    public double TotalMilliseconds { get; init; }

    /// <summary>How many calls fell back — timed out, cancelled, or threw.</summary>
    public long Failures { get; init; }

    public double AverageMilliseconds => Calls == 0 ? 0 : TotalMilliseconds / Calls;
}

/// <summary>
/// Scores how well a candidate answers a query — the job currently done in nine places by
/// <c>ScoreMath.KeywordOverlap</c>, which is Jaccard over a stopword list and cannot tell that
/// "the buoy one" means "Marsh Lane marine sensor project".
///
/// Unlike an embedding, a cross-encoder reads the pair together, which is what lets it judge
/// relatedness rather than similarity. Scores are comparable within one call's candidate set and
/// nowhere else — rank with them, never threshold across different queries.
/// </summary>
public interface ITextPairScorer : ICognitiveModel
{
    /// <summary>
    /// Scores each candidate against the query, in the order given. Returns an empty list when the
    /// model is unavailable, so callers fall back rather than branching on it twice.
    /// </summary>
    Task<IReadOnlyList<double>> ScoreAsync(
        string query, IReadOnlyList<string> candidates, CancellationToken ct = default);
}

/// <summary>The three-way judgment of whether a premise supports, contradicts, or says nothing about a hypothesis.</summary>
public enum Entailment
{
    /// <summary>The premise neither supports nor contradicts it — two unrelated facts about one person.</summary>
    Neutral,

    /// <summary>The premise supports the hypothesis.</summary>
    Entailment,

    /// <summary>The premise and the hypothesis cannot both be true.</summary>
    Contradiction,
}

/// <summary>A judgment plus how sure the model was of it.</summary>
public readonly record struct EntailmentVerdict(Entailment Label, double Confidence)
{
    /// <summary>The answer when no model is available: says nothing, with no confidence.</summary>
    public static EntailmentVerdict Unknown => new(Entailment.Neutral, 0.0);
}

/// <summary>
/// Natural-language inference: does the premise entail, contradict, or say nothing about the
/// hypothesis?
///
/// This is the one judgment in the memory pipeline that the current approach is not merely crude at
/// but structurally unable to make. Measured on this project's embedding model, the preference
/// change that MUST supersede scores 0.763 while two dislikes that MUST coexist score 0.753 — no
/// threshold separates them, because similarity is not the question. Contradiction is.
///
/// It proposes. <c>MemoryCurator</c> still decides, still requires user evidence, and still keeps
/// the old value as linked history: a model that can be wrong never destroys anything.
/// </summary>
public interface INliModel : ICognitiveModel
{
    Task<EntailmentVerdict> ClassifyAsync(string premise, string hypothesis, CancellationToken ct = default);
}

/// <summary>One label a classifier produced, and how strongly.</summary>
public readonly record struct SignalScore(string Label, double Probability);

/// <summary>
/// Multi-label classification over a message: one sentence can be a decision, a correction and a
/// preference change at once, and the detectors it replaces each answer only their own question in
/// isolation.
///
/// Labels are returned as strings here and mapped to typed signals at the edge, so adding a signal
/// to a retrained model doesn't require a code change to be readable — but nothing in the
/// application should branch on a raw label string. See <c>CognitiveSignals</c>.
/// </summary>
public interface ITextClassifier : ICognitiveModel
{
    /// <summary>
    /// Every label above the configured threshold, strongest first. Empty when unavailable.
    /// </summary>
    Task<IReadOnlyList<SignalScore>> ClassifyAsync(string text, CancellationToken ct = default);
}
