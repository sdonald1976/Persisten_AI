using Microsoft.AspNetCore.Mvc.Testing;

namespace Companion.Tests.Fixtures;

/// <summary>
/// Boots the real <c>Companion.Api</c> app for integration tests, offline (Mock provider) against a
/// throwaway database, with a known API token and a single allowed CORS origin. Configuration is
/// supplied via environment variables because the app reads it at startup (before the host is
/// built), which is where <see cref="WebApplicationFactory{T}"/>'s config callbacks can't reach.
/// </summary>
public sealed class CompanionApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-abc123def456";
    public const string AllowedOrigin = "http://localhost:5173";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"companion-api-test-{Guid.NewGuid():N}.db");

    // xUnit constructs collection fixtures by reflection and requires exactly ONE public
    // constructor, parameterless — an optional parameter does not count, and a second public
    // overload is rejected outright. The day this gained `string? userId = null`, every test in
    // the ApiSecurity, ProjectCurationApi and WebSocketGreeting collections failed with
    // "unresolved constructor arguments". The synthetic evaluation, which is not a fixture and
    // wants a caller-chosen user id, goes through ForUser instead.
    public CompanionApiFactory() : this(null)
    {
    }

    /// <summary>A factory for a specific user id — for direct construction, never as a fixture.</summary>
    public static CompanionApiFactory ForUser(string userId) => new(userId);

    private CompanionApiFactory(string? userId)
    {
        Environment.SetEnvironmentVariable("Database__Path", _dbPath);
        Environment.SetEnvironmentVariable("Models__Provider", "Mock");
        Environment.SetEnvironmentVariable("User__Id", userId ?? "demo-user");
        Environment.SetEnvironmentVariable("Api__AuthEnabled", "true");
        Environment.SetEnvironmentVariable("Api__Token", Token);
        Environment.SetEnvironmentVariable("Api__AllowedOrigins__0", AllowedOrigin);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var f in Directory.GetFiles(
                     Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
