using System.Text.Json;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>How a unit of work ended. Terminal states are never retried on resume.</summary>
public enum LedgerState { Pending, Accepted, Rejected, ManualReview, Failed }

public sealed record LedgerEntry
{
    public required string ScenarioId { get; init; }
    public required int VariantIndex { get; init; }
    public required LedgerState State { get; init; }

    /// <summary>Machine-readable terminal reason. Never critic prose.</summary>
    public string? FailureCode { get; init; }

    public string? Detail { get; init; }
    public required string CompletedAtUtc { get; init; }

    public string Key => $"{ScenarioId}#{VariantIndex}";
}

/// <summary>
/// The resumable job ledger: an append-only record of every unit of work that reached a terminal
/// state, so a restart continues instead of regenerating.
///
/// Append-only is what makes it safe. A resumed run never rewrites a line, so a crash mid-write
/// can lose at most the last record — it cannot corrupt an accepted row or duplicate one. On load,
/// the last state for a key wins, which means a deliberate re-run of one scenario supersedes its
/// earlier verdict without the file having to be edited.
///
/// The accepted-row store is keyed by the same id, so "already done" and "already written" cannot
/// disagree: <see cref="ShouldSkip"/> is the only thing the generator asks.
/// </summary>
public sealed class JobLedger
{
    private readonly string _path;
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// String enums, deliberately. The ledger is an operator-readable file that survives crashes
    /// and gets inspected by hand; "state":3 is unreadable, and a numeric enum silently changes
    /// meaning if a case is ever inserted into the middle of the enum.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private JobLedger(string path) => _path = path;

    public static JobLedger Open(string path)
    {
        var ledger = new JobLedger(path);
        if (!File.Exists(path))
            return ledger;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            LedgerEntry? entry;
            try { entry = JsonSerializer.Deserialize<LedgerEntry>(line, Json); }
            catch (JsonException)
            {
                // A torn last line from a kill mid-append. Dropping it is correct: its unit of
                // work simply looks unfinished and gets done again.
                continue;
            }
            if (entry is not null)
                ledger._entries[entry.Key] = entry;      // last write wins
        }
        return ledger;
    }

    /// <summary>Work already finished. A resumed run does not touch these.</summary>
    public bool ShouldSkip(string scenarioId, int variantIndex)
        => _entries.TryGetValue($"{scenarioId}#{variantIndex}", out var e)
           && e.State is not LedgerState.Pending;

    public LedgerEntry? Lookup(string scenarioId, int variantIndex)
        => _entries.GetValueOrDefault($"{scenarioId}#{variantIndex}");

    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

    public int Count(LedgerState state) => _entries.Values.Count(e => e.State == state);

    /// <summary>
    /// Records a terminal verdict. Flushed immediately: the whole point is surviving a kill, and
    /// a buffered ledger is a ledger that lies about what was done.
    /// </summary>
    public void Record(LedgerEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.Key] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(entry, Json));
            writer.Flush();
        }
    }
}

/// <summary>
/// Append-safe row storage. Accepted, rejected and manual-review rows go to separate stores so
/// nothing downstream has to filter by a status field and get it wrong.
/// </summary>
public sealed class RowStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public string Directory => directory;

    /// <summary>
    /// Writes the row and its metadata to two different files. The separation is the guarantee
    /// that no seed, score, rationale or hidden state can reach a training export: the export
    /// reads the rows file and has no access to the other one.
    /// </summary>
    public void Append(Disposition disposition, TrainingRow row, TrainingRowMetadata metadata)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(directory);
            var name = disposition switch
            {
                Disposition.Accepted => "accepted",
                Disposition.Rejected => "rejected",
                _ => "manual-review",
            };
            File.AppendAllText(
                Path.Combine(directory, $"{name}.rows.jsonl"),
                JsonSerializer.Serialize(row, Json) + "\n");
            File.AppendAllText(
                Path.Combine(directory, $"{name}.metadata.jsonl"),
                JsonSerializer.Serialize(metadata, Json) + "\n");
        }
    }

    public IEnumerable<TrainingRow> ReadRows(Disposition disposition)
    {
        var name = disposition switch
        {
            Disposition.Accepted => "accepted",
            Disposition.Rejected => "rejected",
            _ => "manual-review",
        };
        var path = Path.Combine(directory, $"{name}.rows.jsonl");
        if (!File.Exists(path))
            yield break;
        foreach (var line in File.ReadLines(path))
            if (!string.IsNullOrWhiteSpace(line)
                && JsonSerializer.Deserialize<TrainingRow>(line, Json) is { } row)
                yield return row;
    }

    public IEnumerable<TrainingRowMetadata> ReadMetadata(Disposition disposition)
    {
        var name = disposition switch
        {
            Disposition.Accepted => "accepted",
            Disposition.Rejected => "rejected",
            _ => "manual-review",
        };
        var path = Path.Combine(directory, $"{name}.metadata.jsonl");
        if (!File.Exists(path))
            yield break;
        foreach (var line in File.ReadLines(path))
            if (!string.IsNullOrWhiteSpace(line)
                && JsonSerializer.Deserialize<TrainingRowMetadata>(line, Json) is { } m)
                yield return m;
    }
}
