using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// LLM-backed second-pass retrieval judge. It sees only the already-eligible memories and may
/// reorder or omit them; malformed output falls back to the original hybrid ranking.
/// </summary>
public sealed class LlmMemoryReranker : IMemoryReranker
{
    private const int MaxCandidates = 30;
    private const int MaxContentChars = 700;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IChatModel _chat;
    private readonly IMemoryReranker _fallback;
    private readonly ILogger<LlmMemoryReranker> _logger;

    public LlmMemoryReranker(IChatModel chat, IMemoryReranker fallback, ILogger<LlmMemoryReranker> logger)
    {
        _chat = chat;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query, IReadOnlyList<RetrievalResult> candidates, int maxResults,
        CancellationToken ct = default)
    {
        if (candidates.Count <= 1)
            return candidates.Take(maxResults).ToList();

        var limited = candidates.Take(MaxCandidates).ToList();
        var byId = limited.ToDictionary(c => c.Memory.Id.ToString("N"), c => c);

        var prompt = new StringBuilder();
        prompt.AppendLine($"User message: {query}");
        prompt.AppendLine();
        prompt.AppendLine("Candidate memories:");
        foreach (var c in limited)
        {
            var content = c.Memory.Content;
            if (content.Length > MaxContentChars)
                content = content[..MaxContentChars];
            prompt.AppendLine($"- id: {c.Memory.Id:N}");
            prompt.AppendLine($"  memory: {content}");
            prompt.AppendLine($"  originalScore: {c.Score:F3}");
            prompt.AppendLine($"  reason: {c.Reason}");
        }
        prompt.AppendLine();
        prompt.AppendLine($"Return the best {maxResults} memory ids, most relevant first.");

        try
        {
            var raw = (await _chat.CompleteAsync(SystemPrompt, prompt.ToString(), jsonMode: true, ct: ct)).Text;
            var ids = TryParseIds(raw);
            if (ids.Count == 0)
                return await _fallback.RerankAsync(query, candidates, maxResults, ct);

            var selected = new List<RetrievalResult>();
            foreach (var id in ids)
            {
                var key = id.Replace("-", "", StringComparison.Ordinal).Trim();
                if (byId.TryGetValue(key, out var result) && !selected.Contains(result))
                    selected.Add(result);
                if (selected.Count >= maxResults)
                    break;
            }

            foreach (var candidate in limited)
            {
                if (selected.Count >= maxResults)
                    break;
                if (!selected.Contains(candidate))
                    selected.Add(candidate);
            }

            return selected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory reranker failed; preserving hybrid retrieval order.");
            return await _fallback.RerankAsync(query, candidates, maxResults, ct);
        }
    }

    private static List<string> TryParseIds(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFence(raw));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray().Select(ReadId).Where(s => s is not null).Cast<string>().ToList();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ids", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
                return ids.EnumerateArray().Select(ReadId).Where(s => s is not null).Cast<string>().ToList();
        }
        catch (JsonException) { }
        return new List<string>();
    }

    private static string? ReadId(JsonElement element)
        => element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;

    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0)
            return t;
        t = t[(firstNewline + 1)..];
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? t[..lastFence] : t;
    }

    private const string SystemPrompt =
        "You are a memory reranker. Choose only memories that directly help answer the user's " +
        "current message. Prefer precise relevance over recency or general importance. Return ONLY " +
        "JSON: {\"ids\":[\"memory-id\", ...]}. You may omit weak or unrelated candidates.";
}
