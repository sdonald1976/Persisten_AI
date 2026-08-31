using System.Text.Json;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// Appends shadow reranker comparisons to a local JSONL file — the corpus the review and
/// evaluation tools read. Ids and orderings only; no memory or query text. Local by design: the
/// file stays on this machine, and the review tool joins it against the local memory store to
/// show content, so nothing sensitive is duplicated here.
///
/// Writing never throws into the turn: a failed append is logged and dropped, because a shadow
/// experiment must never affect the reply.
/// </summary>
public sealed class JsonlRerankShadowSink : IRerankShadowSink
{
    private readonly string _path;
    private readonly ILogger<JsonlRerankShadowSink> _logger;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public JsonlRerankShadowSink(string path, ILogger<JsonlRerankShadowSink> logger)
    {
        _path = path;
        _logger = logger;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create rerank-shadow directory {Dir}.",
                Path.GetDirectoryName(_path));
        }
    }

    public bool IsRecording => true;

    public void Record(RerankShadowRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, Json);
            lock (_lock)
                File.AppendAllText(_path, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append a rerank-shadow record; dropped.");
        }
    }
}
