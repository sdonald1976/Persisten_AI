using System.Text.Json;

namespace Companion.Core.Abstractions;

/// <summary>
/// One capability the chat model may intentionally invoke mid-turn. Tools are the controlled door
/// between the model and the infrastructure: user-meaningful names ("memory.search"), validated
/// arguments, structured results, and — for this entire first generation of tools — strictly
/// read-only behavior. The model never touches stores directly, never supplies a user id, and
/// never gets a tool the installation doesn't actually have.
/// </summary>
public interface ICompanionTool
{
    /// <summary>Stable dotted name, e.g. "memory.search".</summary>
    string Name { get; }

    /// <summary>One or two sentences the model reads to decide when this tool helps.</summary>
    string Description { get; }

    /// <summary>Human-readable arguments shape, e.g. {"query": "text", "limit": "1-8 (optional)"}.</summary>
    string ArgumentsHint { get; }

    /// <summary>Whether this installation can actually run the tool right now.</summary>
    bool Available { get; }

    /// <summary>
    /// Executes with MODEL-supplied arguments (untrusted, validated inside) for the
    /// APPLICATION-supplied user (trusted, never from the model). Must never throw for bad
    /// arguments — return a failed <see cref="ToolResult"/> instead.
    /// </summary>
    Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default);
}

/// <summary>A structured tool outcome: machine-readable success/failure plus a JSON-friendly payload.</summary>
public sealed record ToolResult
{
    public required bool Ok { get; init; }

    /// <summary>"ok", or a failure code: invalid_arguments | not_found | unavailable | provider_failure | timeout.</summary>
    public required string Code { get; init; }

    /// <summary>The structured payload on success; a short safe message on failure.</summary>
    public object? Data { get; init; }

    public static ToolResult Success(object data) => new() { Ok = true, Code = "ok", Data = data };
    public static ToolResult Fail(string code, string message) => new() { Ok = false, Code = code, Data = new { message } };
}
