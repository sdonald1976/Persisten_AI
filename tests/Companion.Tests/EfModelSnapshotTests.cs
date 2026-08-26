using System.Text;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Snapshot of the built EF <c>IModel</c> — every entity, property, key and index.
///
/// The audit proposes splitting <c>CompanionDbContext</c>'s single 568-line
/// <c>OnModelCreating</c> (41 DbSets across nine domains) into per-aggregate
/// <c>IEntityTypeConfiguration</c> classes. That refactor is safe if and only if the
/// resulting model is identical, and "identical" is not something a reviewer can check by
/// reading a diff of moved code — a dropped <c>HasMaxLength</c> or a lost index looks
/// exactly like a successful move.
///
/// So the model is pinned as text. The mapping split must change this file not at all.
/// </summary>
public class EfModelSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static string SnapshotPath => Path.Combine(
        RepoRoot(), "tests", "Companion.Tests", "Goldens", "ef-model.txt");

    private static string Describe(IModel model)
    {
        var sb = new StringBuilder();
        sb.Append("# EF model snapshot. The mapping split must not change this file.\n");

        foreach (var entity in model.GetEntityTypes()
                     .OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            sb.Append("\n===== ").Append(entity.Name)
              .Append("  table=").Append(entity.GetTableName()).Append(" =====\n");

            foreach (var p in entity.GetProperties()
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append("  prop ").Append(p.Name)
                  .Append(" : ").Append(p.ClrType.Name)
                  .Append(p.IsNullable ? " null" : " notnull")
                  .Append(" col=").Append(p.GetColumnType());
                if (p.GetMaxLength() is { } max)
                    sb.Append(" max=").Append(max);
                if (p.IsConcurrencyToken)
                    sb.Append(" concurrency");
                if (p.ValueGenerated != ValueGenerated.Never)
                    sb.Append(" generated=").Append(p.ValueGenerated);
                sb.Append('\n');
            }

            foreach (var key in entity.GetKeys()
                         .OrderBy(k => string.Join(",", k.Properties.Select(p => p.Name)),
                             StringComparer.Ordinal))
                sb.Append("  key ")
                  .Append(key.IsPrimaryKey() ? "primary " : "alternate ")
                  .Append(string.Join(",", key.Properties.Select(p => p.Name)))
                  .Append('\n');

            foreach (var index in entity.GetIndexes()
                         .OrderBy(i => string.Join(",", i.Properties.Select(p => p.Name)),
                             StringComparer.Ordinal))
                sb.Append("  index ")
                  .Append(index.IsUnique ? "unique " : "")
                  .Append(string.Join(",", index.Properties.Select(p => p.Name)))
                  .Append('\n');

            foreach (var fk in entity.GetForeignKeys()
                         .OrderBy(f => string.Join(",", f.Properties.Select(p => p.Name)),
                             StringComparer.Ordinal))
                sb.Append("  fk ")
                  .Append(string.Join(",", fk.Properties.Select(p => p.Name)))
                  .Append(" -> ").Append(fk.PrincipalEntityType.Name)
                  .Append(" onDelete=").Append(fk.DeleteBehavior)
                  .Append('\n');
        }

        return sb.ToString();
    }

    public static async Task<string> CurrentAsync()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        return Describe(db.Model);
    }

    [Fact]
    public async Task TheEntityModel_IsExactlyAsPinned()
    {
        Assert.True(File.Exists(SnapshotPath),
            $"snapshot missing at {SnapshotPath}. Generate it deliberately and commit it as "
            + "a reviewed change.");

        var expected = File.ReadAllText(SnapshotPath).ReplaceLineEndings("\n");
        var actual = (await CurrentAsync()).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TheSnapshotCoversEveryDbSet()
    {
        // A snapshot that silently stopped covering an entity would be worse than none.
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var mapped = db.Model.GetEntityTypes().Count();
        var dbSets = typeof(Infrastructure.Persistence.CompanionDbContext)
            .GetProperties()
            .Count(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));

        Assert.True(mapped >= dbSets,
            $"{dbSets} DbSets but only {mapped} mapped entity types");
        Assert.True(dbSets >= 40, $"expected the full schema; saw {dbSets} DbSets");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
