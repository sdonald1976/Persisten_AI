namespace Companion.Core.Domain;

/// <summary>
/// One reranker's ordering of a candidate set, plus how it ran. Scores are populated only where
/// they are comparable across methods (the cross-encoder and the rule expose per-item scores; the
/// 3B does not, so its <see cref="Scores"/> is empty and only its <see cref="Ranking"/> is used).
/// </summary>
public sealed record RerankMethodResult
{
    /// <summary>"authoritative-3b", "cross-encoder", or "rule".</summary>
    public required string Method { get; init; }

    /// <summary>Candidate memory ids in the order this method ranked them (best first).</summary>
    public required IReadOnlyList<Guid> Ranking { get; init; }

    /// <summary>Per-id score where the method exposes one; empty otherwise.</summary>
    public IReadOnlyDictionary<Guid, double> Scores { get; init; } =
        new Dictionary<Guid, double>();

    public required double LatencyMs { get; init; }

    /// <summary>True when this method failed or timed out and its ranking is a fallback/empty.</summary>
    public bool Failed { get; init; }

    public string? FailureReason { get; init; }
}

/// <summary>
/// A shadow comparison of all three rerankers on one eligible retrieval event. Stores IDS ONLY —
/// no memory text and no query text. Review and evaluation join these ids against the local
/// memory store and turn record to show content, so nothing is duplicated. Private turns are
/// excluded at the source (the turn scope carries the record-permission flag).
///
/// Only the authoritative method affected the displayed turn; the others are observation.
/// </summary>
public sealed record RerankShadowRecord
{
    public const int SchemaVersion = 1;

    public required Guid TurnId { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The candidate memory ids considered, in retrieval order. The candidate-SET
    /// identity is these ids; two events with the same set are comparable.</summary>
    public required IReadOnlyList<Guid> CandidateIds { get; init; }

    /// <summary>Stable hash of the candidate set (order-independent), for grouping/dedup.</summary>
    public required string CandidateSetHash { get; init; }

    public required RerankMethodResult Authoritative { get; init; }
    public RerankMethodResult? CrossEncoder { get; init; }
    public RerankMethodResult? Rule { get; init; }

    /// <summary>Top-1 id agreement flags between the authoritative method and each shadow.</summary>
    public bool CrossEncoderTop1Agrees =>
        CrossEncoder is { Ranking.Count: > 0 } ce && Authoritative.Ranking.Count > 0
        && ce.Ranking[0] == Authoritative.Ranking[0];

    public bool RuleTop1Agrees =>
        Rule is { Ranking.Count: > 0 } r && Authoritative.Ranking.Count > 0
        && r.Ranking[0] == Authoritative.Ranking[0];

    public int SchemaVersionField { get; init; } = SchemaVersion;
}

/// <summary>
/// Where shadow reranker comparisons go. File-backed in practice (a local jsonl the review and
/// eval tools read); an interface so the turn path depends on nothing heavier and tests can use
/// an in-memory sink. Writing must never throw into the turn.
/// </summary>
public interface IRerankShadowSink
{
    /// <summary>True when recording is enabled; callers skip building a record when false.</summary>
    bool IsRecording { get; }

    void Record(RerankShadowRecord record);
}

/// <summary>The no-op sink: recording off. The default, so a build with the flag off writes nothing.</summary>
public sealed class NullRerankShadowSink : IRerankShadowSink
{
    public bool IsRecording => false;

    public void Record(RerankShadowRecord record) { }
}
