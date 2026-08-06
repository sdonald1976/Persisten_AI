using Microsoft.Data.Sqlite;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// Operational safety for the SQLite database: automatic backup before a migration, snapshot
/// export, and validated import. A user should never lose accumulated memories because the schema
/// evolved — migrations back up first, and export/import give an explicit, safe copy path.
/// </summary>
public static class DatabaseMaintenance
{
    /// <summary>Copies <paramref name="dbPath"/> to a timestamped ".bak" beside it. Returns the backup path, or null if there was nothing to back up.</summary>
    public static string? Backup(string dbPath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return null;

        var backup = $"{dbPath}.bak-{now:yyyyMMdd-HHmmss}";
        File.Copy(dbPath, backup, overwrite: true);
        return backup;
    }

    /// <summary>
    /// Exports a consistent snapshot of the database to <paramref name="destination"/> using SQLite's
    /// <c>VACUUM INTO</c> (safe even while the app holds the DB open). Overwrites an existing file.
    /// </summary>
    public static void Export(string dbPath, string destination)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"No database to export at '{dbPath}'.", dbPath);

        var full = Path.GetFullPath(destination);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(full))
            File.Delete(full); // VACUUM INTO requires the target not to exist

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $dest";
        command.Parameters.AddWithValue("$dest", full);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Replaces the database with a snapshot at <paramref name="source"/>, after validating it looks
    /// like a companion database and backing up the current one. Returns the backup path (if any).
    /// </summary>
    public static string? Import(string dbPath, string source, DateTimeOffset now)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException($"No snapshot to import at '{source}'.", source);

        if (!LooksLikeCompanionDatabase(source))
            throw new InvalidOperationException(
                $"'{source}' does not look like a companion database (missing the migrations history / core tables).");

        var backup = Backup(dbPath, now);

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(source, dbPath, overwrite: true);
        return backup;
    }

    /// <summary>True if the file is a SQLite DB containing the EF migrations history table.</summary>
    public static bool LooksLikeCompanionDatabase(string path)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('__EFMigrationsHistory','Messages');";
            var count = Convert.ToInt64(command.ExecuteScalar());
            return count >= 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
