using Companion.Core.Domain;
using Companion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Companion.Tests;

/// <summary>H8: the safe database upgrade path — backup, export snapshot, validated import roundtrip.</summary>
public class DatabaseMaintenanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"companion-dbmaint-{Guid.NewGuid():N}");

    public DatabaseMaintenanceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static CompanionDbContext Open(string dbPath)
    {
        // Pooling=False so each context fully closes its file handle on dispose; otherwise a pooled
        // connection would keep serving the pre-import (cached) database in this same test process.
        var options = new DbContextOptionsBuilder<CompanionDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False").Options;
        return new CompanionDbContext(options);
    }

    private async Task<Guid> CreateSeededDbAsync(string dbPath)
    {
        await using var db = Open(dbPath);
        await db.Database.MigrateAsync();
        var conv = new Conversation
        {
            Id = Guid.NewGuid(), UserId = "u", Title = "keepsake",
            StartedAt = Now, LastActivityAt = Now,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task Export_ProducesAValidCompanionSnapshot()
    {
        var dbPath = Path_("companion.db");
        await CreateSeededDbAsync(dbPath);

        var snapshot = Path_("snapshot.db");
        DatabaseMaintenance.Export(dbPath, snapshot);

        Assert.True(File.Exists(snapshot));
        Assert.True(DatabaseMaintenance.LooksLikeCompanionDatabase(snapshot));
    }

    [Fact]
    public async Task Backup_CopiesTheExistingDatabase()
    {
        var dbPath = Path_("companion.db");
        await CreateSeededDbAsync(dbPath);

        var backup = DatabaseMaintenance.Backup(dbPath, Now);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void Backup_OfMissingDatabase_ReturnsNull()
        => Assert.Null(DatabaseMaintenance.Backup(Path_("nope.db"), Now));

    [Fact]
    public async Task ImportRoundtrip_RestoresData_AndBacksUpTheReplacedDatabase()
    {
        var source = Path_("source.db");
        var keepsakeId = await CreateSeededDbAsync(source);
        var snapshot = Path_("snapshot.db");
        DatabaseMaintenance.Export(source, snapshot);

        // A different existing database that we import over — it should be backed up first.
        var target = Path_("target.db");
        await CreateSeededDbAsync(target);

        var backup = DatabaseMaintenance.Import(target, snapshot, Now);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));

        // The imported target now contains the source's data.
        await using var db = Open(target);
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == keepsakeId));
    }

    [Fact]
    public void Import_OfNonCompanionFile_IsRejected()
    {
        var garbage = Path_("garbage.db");
        File.WriteAllText(garbage, "this is not a sqlite database");
        Assert.Throws<InvalidOperationException>(
            () => DatabaseMaintenance.Import(Path_("companion.db"), garbage, Now));
    }
}
