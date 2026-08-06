namespace Companion.Api;

/// <summary>
/// Security-relevant API configuration (bound from the "Api" section). Secure-by-default:
/// authentication is on, and CORS is an explicit allow-list — no wildcard origins.
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// When true (default), every REST/SSE/WebSocket call must present the local API token.
    /// Set false ONLY for explicit local development.
    /// </summary>
    public bool AuthEnabled { get; set; } = true;

    /// <summary>
    /// The local API token. If empty and auth is enabled, one is generated on first startup and
    /// persisted to <see cref="TokenFile"/> so it stays stable across restarts.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Where a generated token is stored (relative paths resolve next to the database).</summary>
    public string TokenFile { get; set; } = ".companion-api-token";

    /// <summary>Browser origins allowed by CORS. Only these may call the API from a page.</summary>
    public string[] AllowedOrigins { get; set; } =
    {
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    };

    /// <summary>Default loopback binding — never exposed on LAN interfaces unless the host is overridden.</summary>
    public string DefaultUrl { get; set; } = "http://127.0.0.1:5266";
}
