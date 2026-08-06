using System.Security.Cryptography;
using System.Text;
using Companion.Core.Domain;
using Companion.Infrastructure.Models;

namespace Companion.Api;

/// <summary>Sanitized error contract returned to clients — no raw exception text ever leaves the server.</summary>
public sealed record ApiError(string Error, string Message, string CorrelationId)
{
    /// <summary>A sanitized error <see cref="IResult"/> with a fresh correlation id (for non-exception paths).</summary>
    public static IResult Result(int status, string code, string message)
        => Results.Json(new ApiError(code, message, Guid.NewGuid().ToString("N")[..8]), statusCode: status);

    public static IResult ConversationNotFound()
        => Result(StatusCodes.Status404NotFound, "ConversationNotFound", "The conversation was not found.");

    public static IResult BadRequest(string message)
        => Result(StatusCodes.Status400BadRequest, "BadRequest", message);

    /// <summary>Maps an exception to a safe (statusCode, error-code, client-message) triple.</summary>
    public static (int Status, string Code, string Message) Classify(Exception ex) => ex switch
    {
        ConversationNotFoundException => (StatusCodes.Status404NotFound, "ConversationNotFound",
            "The conversation was not found."),
        ModelProviderException => (StatusCodes.Status503ServiceUnavailable, "ProviderUnavailable",
            "The configured model provider could not be reached."),
        ArgumentException => (StatusCodes.Status400BadRequest, "BadRequest",
            "The request was invalid."),
        _ => (StatusCodes.Status500InternalServerError, "InternalError",
            "An unexpected error occurred."),
    };
}

/// <summary>Local API token handling: resolve/generate/persist, and constant-time verification.</summary>
public static class ApiToken
{
    /// <summary>
    /// Returns the configured token, or generates a random one and persists it to the token file
    /// (next to the database) so it is stable across restarts. Returns null when auth is disabled.
    /// </summary>
    public static string? Resolve(ApiOptions options, string dbPath, ILogger logger)
    {
        if (!options.AuthEnabled)
        {
            logger.LogWarning("API authentication is DISABLED (Api:AuthEnabled=false). Use only for local development.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.Token))
            return options.Token;

        var path = ResolveTokenPath(options.TokenFile, dbPath);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        try
        {
            File.WriteAllText(path, token);
            logger.LogInformation("Generated a local API token and saved it to {Path}. Send it as 'Authorization: Bearer <token>'.", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist the generated API token to {Path}; it will change on restart.", path);
        }
        return token;
    }

    private static string ResolveTokenPath(string tokenFile, string dbPath)
    {
        if (Path.IsPathRooted(tokenFile))
            return tokenFile;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        return string.IsNullOrEmpty(dir) ? tokenFile : Path.Combine(dir, tokenFile);
    }

    /// <summary>Extracts a presented token from the Authorization/X-Companion-Key header or access_token query.</summary>
    public static string? FromRequest(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        var key = request.Headers["X-Companion-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(key))
            return key.Trim();

        // SSE (EventSource) and browser WebSockets cannot set custom headers, so allow a query token.
        var q = request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    }

    public static bool Verify(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented))
            return false;
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
