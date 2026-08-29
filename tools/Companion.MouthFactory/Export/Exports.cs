using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Companion.MouthFactory.Schema;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Companion.MouthFactory.Export;

/// <summary>
/// Training-ready exports.
///
/// Only <see cref="TrainingRow"/> is ever written — system, input, target, format version. The
/// metadata store is not opened by this class at all, which is the structural reason a seed, a
/// score, a critic's rationale or a scenario's hidden state cannot reach a training file.
/// </summary>
public static class Exports
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string WriteJsonl(string directory, string name, IReadOnlyList<TrainingRow> rows)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.jsonl");
        // LF, explicitly. WriteLine defaults to Environment.NewLine, which would make the corpus
        // - and therefore every hash in SHA256SUMS - depend on the operating system that wrote it.
        // "the same pool yields the same corpus on any machine" has to be true of the bytes.
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8)
        {
            NewLine = "\n",
        };
        foreach (var row in rows)
            writer.WriteLine(JsonSerializer.Serialize(row, Json));
        return path;
    }

    public static async Task<string> WriteParquetAsync(
        string directory, string name, IReadOnlyList<TrainingRow> rows, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.parquet");

        var idField = new DataField<string>("id");
        var systemField = new DataField<string>("system");
        var inputField = new DataField<string>("input");
        var targetField = new DataField<string>("target");
        var formatField = new DataField<string>("formatVersion");
        var schema = new ParquetSchema(idField, systemField, inputField, targetField, formatField);

        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: ct);
        using var group = writer.CreateRowGroup();

        await group.WriteColumnAsync(new DataColumn(idField, rows.Select(r => r.Id).ToArray()), ct);
        await group.WriteColumnAsync(new DataColumn(systemField, rows.Select(r => r.System).ToArray()), ct);
        await group.WriteColumnAsync(new DataColumn(inputField, rows.Select(r => r.Input).ToArray()), ct);
        await group.WriteColumnAsync(new DataColumn(targetField, rows.Select(r => r.Target).ToArray()), ct);
        await group.WriteColumnAsync(new DataColumn(formatField, rows.Select(r => r.FormatVersion).ToArray()), ct);

        return path;
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// Source provenance. A family with no completed manifest contributes no rows — the check is
/// mechanical, so "we'll fill it in later" cannot produce a corpus.
/// </summary>
public sealed record SourceManifest
{
    public required string FamilyId { get; init; }

    /// <summary>Where it came from: a dataset name, a repository, or "generated".</summary>
    public required string Origin { get; init; }

    /// <summary>Exact revision or snapshot id. "latest" is not a snapshot.</summary>
    public required string Revision { get; init; }

    public required string License { get; init; }

    /// <summary>What that licence actually permits here, in plain words.</summary>
    public required string PermittedUse { get; init; }

    /// <summary>What the factory did to it between source and distilled row.</summary>
    public required string Transformations { get; init; }

    public required int RowCount { get; init; }

    public bool Complete =>
        !string.IsNullOrWhiteSpace(FamilyId) && !string.IsNullOrWhiteSpace(Origin)
        && !string.IsNullOrWhiteSpace(Revision) && !string.IsNullOrWhiteSpace(License)
        && !string.IsNullOrWhiteSpace(PermittedUse) && !string.IsNullOrWhiteSpace(Transformations);
}

/// <summary>
/// Sources that may not be acquired without explicit approval, and the reason. Named so an
/// operator who reaches for one gets a refusal that says why rather than a vague policy.
/// </summary>
public static class SourcePolicy
{
    public static readonly IReadOnlyDictionary<string, string> Prohibited =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["opensubtitles"] = "licence unclear for redistribution and derivative training data",
            ["writingprompts"] = "Reddit-derived; contributor licensing not established",
            ["cornell movie-dialogs"] = "research-only terms; not cleared for model training",
            ["cornell"] = "research-only terms; not cleared for model training",
            ["scraped roleplay"] = "no licence and no contributor consent",
            ["pygmalion"] = "provenance of constituent logs not established",
        };

    /// <summary>Licence families that need approval regardless of source.</summary>
    public static readonly string[] RestrictedLicenses = ["nc", "noncommercial", "sharealike", "sa-", "cc-by-nc"];

    public static string? Refuse(SourceManifest manifest)
    {
        foreach (var (name, why) in Prohibited)
            if (manifest.Origin.Contains(name, StringComparison.OrdinalIgnoreCase))
                return $"source '{manifest.Origin}' requires explicit approval: {why}";

        foreach (var restricted in RestrictedLicenses)
            if (manifest.License.Contains(restricted, StringComparison.OrdinalIgnoreCase))
                return $"licence '{manifest.License}' is NC/ShareAlike and requires explicit approval";

        return manifest.Complete ? null : $"source manifest for '{manifest.FamilyId}' is incomplete";
    }
}

/// <summary>What one generation run did. Written beside the exports.</summary>
public sealed record RunManifest
{
    public required string RunId { get; init; }
    public required string StartedUtc { get; init; }
    public required string SchemaVersion { get; init; }
    public required string RowSchemaVersion { get; init; }
    public required string PromptFormatVersion { get; init; }
    public required string RepoCommit { get; init; }
    public required IReadOnlyDictionary<string, string> Roles { get; init; }
    public required int Generated { get; init; }
    public required int Accepted { get; init; }
    public required int Rejected { get; init; }
    public required int ManualReview { get; init; }
    public required IReadOnlyList<SourceManifest> Sources { get; init; }
    public IReadOnlyDictionary<string, string> ExportHashes { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
}
