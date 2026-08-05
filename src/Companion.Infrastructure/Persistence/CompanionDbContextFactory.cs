using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef</c> can construct the context when generating migrations.
/// The connection string here is only used at design time; the running app supplies its own.
/// </summary>
public sealed class CompanionDbContextFactory : IDesignTimeDbContextFactory<CompanionDbContext>
{
    public CompanionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CompanionDbContext>()
            .UseSqlite("Data Source=companion-design.db")
            .Options;
        return new CompanionDbContext(options);
    }
}
