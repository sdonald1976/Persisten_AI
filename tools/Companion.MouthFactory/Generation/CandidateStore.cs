using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// Where a candidate is in its life. Explicit and persisted, because stage batching means a
/// candidate now outlives the process that produced it.
/// </summary>
public enum CandidateState
{
    /// <summary>Written by the writer, durably stored, no critic has run.</summary>
    GeneratedPendingCritics,

    /// <summary>Every required critic returned a verdict and all accepted.</summary>
    Accepted,

    /// <summary>A deterministic check rejected it. Critics were never consulted.</summary>
    Rejected,

    /// <summary>At least one critic disagreed. Never silently accepted.</summary>
    ManualReview,

    /// <summary>Generation or rendering failed. Terminal.</summary>
    Failed,
}

/// <summary>One critic's verdict, persisted independently so a crash loses at most one.</summary>
public sealed record CriticVerdict
{
    public required string Role { get; init; }
    public required string Model { get; init; }
    public required bool Passed { get; init; }
    public string? Code { get; init; }

    /// <summary>Diagnostic only. Never exported, never near a training target.</summary>
    public string? Detail { get; init; }

    public required string AtUtc { get; init; }
}

/// <summary>
/// A durably stored candidate: everything needed to finish judging it in a later process, and
/// everything needed to prove it still belongs to the truth it was generated from.
///
/// The identity hashes are the point. Stage batching separates generation from criticism in
/// time, so a resumed run could otherwise attach yesterday's candidate to a scenario that has
/// since been regenerated with different hidden state — and the verdicts would be about a plan
/// nobody ever wrote. Both hashes are checked before criticism proceeds.
/// </summary>
public sealed record PendingCandidate
{
    public required string Id { get; init; }                 // "{scenarioId}#{variant}"
    public required string ScenarioId { get; init; }
    public required string ScenarioFamilyId { get; init; }
    public required string FamilyId { get; init; }
    public required int VariantIndex { get; init; }

    /// <summary>Hash of the scenario truth this was generated from.</summary>
    public required string ScenarioHash { get; init; }

    /// <summary>Hash of the exact CompactV4 user message the writer was given.</summary>
    public required string InputHash { get; init; }

    /// <summary>The exact bytes the model will be trained on, stored whole.</summary>
    public required TrainingRow Row { get; init; }

    public required TrainingRowMetadata Metadata { get; init; }

    /// <summary>Which critic roles must return a verdict before this can be terminal.</summary>
    public required IReadOnlyList<string> RequiredCritics { get; init; }

    public IReadOnlyList<CriticVerdict> Verdicts { get; init; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CandidateState State { get; init; }

    public string? TerminalCode { get; init; }
    public required string UpdatedUtc { get; init; }

    /// <summary>Critic roles still owing a verdict.</summary>
    public IReadOnlyList<string> MissingCritics =>
        RequiredCritics.Where(r => Verdicts.All(v => v.Role != r)).ToList();
}

/// <summary>
/// Append-only durable storage for candidates across stages.
///
/// Append-only, and the last record for an id wins. A crash mid-write loses at most the record
/// being written; it cannot corrupt an earlier one, and it cannot duplicate a terminal row
/// because the terminal state is derived from the last record rather than from a separate flag.
/// Each write is flushed, because a buffered ledger is a ledger that lies about what was done.
/// </summary>
public sealed class CandidateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<string, PendingCandidate> _latest = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private CandidateStore(string path) => _path = path;

    public static CandidateStore Open(string path)
    {
        var store = new CandidateStore(path);
        if (!File.Exists(path))
            return store;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<PendingCandidate>(line, Json) is { } c)
                    store._latest[c.Id] = c;
            }
            catch (JsonException)
            {
                // A torn final line from a kill mid-append. Dropping it means that candidate
                // looks un-generated and is simply done again - the safe direction.
            }
        }
        return store;
    }

    public IReadOnlyCollection<PendingCandidate> All => _latest.Values;

    public PendingCandidate? Find(string id) => _latest.GetValueOrDefault(id);

    /// <summary>Candidates awaiting a verdict from this critic role, in stable order.</summary>
    public IReadOnlyList<PendingCandidate> AwaitingCritic(string role)
        => _latest.Values
            .Where(c => c.State == CandidateState.GeneratedPendingCritics
                        && c.RequiredCritics.Contains(role)
                        && c.Verdicts.All(v => v.Role != role))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<PendingCandidate> Pending()
        => _latest.Values
            .Where(c => c.State == CandidateState.GeneratedPendingCritics)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    public int Count(CandidateState state) => _latest.Values.Count(c => c.State == state);

    public void Write(PendingCandidate candidate)
    {
        lock (_gate)
        {
            _latest[candidate.Id] = candidate;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(candidate, Json));
            writer.Flush();
        }
    }

    /// <summary>Stable hash of scenario truth. Changes if any hidden state changes.</summary>
    public static string HashScenario(ScenarioTruth scenario)
        => Sha(JsonSerializer.Serialize(scenario, Json));

    public static string HashInput(string input) => Sha(input);

    private static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant()[..16];
}
