using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// LLM-backed extractor: asks an <see cref="IChatModel"/> for candidate memories as JSON and
/// parses them. Like every extractor it only proposes — the pipeline still validates. Kept
/// behind the same interface so switching from the rule-based default is a DI change only.
/// </summary>
public sealed class LlmMemoryExtractor : IMemoryExtractor
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IChatModel _chat;
    private readonly ILogger<LlmMemoryExtractor> _logger;

    public LlmMemoryExtractor(IChatModel chat, ILogger<LlmMemoryExtractor> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
        string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default)
    {
        var userMessages = exchange.Where(m => m.Role == MessageRole.User).ToList();
        if (userMessages.Count == 0)
            return Array.Empty<MemoryCandidate>();

        var transcript = new StringBuilder();
        foreach (var m in exchange)
            transcript.AppendLine($"{m.Role}: {m.Content}");

        // Ask for JSON explicitly (structured-output mode where the server supports it).
        var raw = (await _chat.CompleteAsync(SystemPrompt, transcript.ToString(), jsonMode: true, ct: ct)).Text;

        // Bound the untrusted body before parsing so a runaway response can't be a problem here.
        if (raw.Length > MaxRawChars)
            raw = raw[..MaxRawChars];

        var dtos = TryParse(raw);
        if (dtos is null)
        {
            _logger.LogWarning("LLM extractor returned unparseable/invalid output; no candidates proposed.");
            return Array.Empty<MemoryCandidate>();
        }

        var candidates = new List<MemoryCandidate>();
        foreach (var dto in dtos)
        {
            if (candidates.Count >= MaxCandidates)
            {
                _logger.LogWarning("LLM extractor proposed more than {Max} candidates; extra ignored.", MaxCandidates);
                break;
            }

            // Validate the model's free text: required, length-capped. The model never supplies
            // ids, ownership, or lifecycle authority — enums fall back and numerics are clamped.
            if (string.IsNullOrWhiteSpace(dto.Content) || dto.Content.Length > MaxFieldChars)
                continue;

            // Provenance must be verifiable: the cited excerpt has to actually appear in one of the
            // user's messages. If it can't be verified we REJECT the candidate (log why) rather
            // than manufacturing evidence — memory only exists when it is genuinely supported.
            var evidence = ResolveEvidence(dto.Excerpt, userMessages);
            if (evidence.Count == 0)
            {
                _logger.LogWarning(
                    "Rejected an extracted candidate: its excerpt could not be verified against any user message.");
                continue;
            }

            candidates.Add(new MemoryCandidate
            {
                Kind = ParseKind(dto.Kind),
                Subject = Cap(dto.Subject),
                Predicate = Cap(dto.Predicate),
                Value = Cap(dto.Value),
                Content = dto.Content.Trim(),
                Validity = ParseEnum(dto.Validity, Validity.Current),
                EpisodeStatus = ParseEnum(dto.EpisodeStatus, EpisodeStatus.Occurred),
                RelatedProject = Cap(dto.RelatedProject),
                Importance = Clamp(dto.Importance, 0.5),
                ProposedConfidence = Clamp(dto.Confidence, 0.5),
                Evidence = evidence,
            });
        }

        return candidates;
    }

    private const int MaxCandidates = 50;
    private const int MaxFieldChars = 2000;
    private const int MaxRawChars = 200_000;

    private static string? Cap(string? value)
        => value is null ? null : value.Length <= MaxFieldChars ? value : value[..MaxFieldChars];

    /// <summary>
    /// Verifies the model-supplied excerpt against the real user messages. Returns evidence only
    /// when the excerpt actually occurs in one of them; otherwise returns empty (no fabrication).
    /// </summary>
    private static List<CandidateEvidence> ResolveEvidence(string? excerpt, List<Message> userMessages)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
            return new List<CandidateEvidence>();

        var match = userMessages.FirstOrDefault(m =>
            m.Content.Contains(excerpt, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? new List<CandidateEvidence>()
            : new List<CandidateEvidence> { new(match.Id, excerpt.Trim()) };
    }

    /// <summary>
    /// Robustly extract the candidate array from model output. Handles a bare array, a markdown
    /// code fence, and a wrapping object (<c>{"memories":[...]}</c> etc.). Does NOT do the fragile
    /// "first [ to last ]" slice, which breaks on brackets inside prose or string values.
    /// </summary>
    private static List<CandidateDto>? TryParse(string raw)
    {
        var text = StripFence(raw).Trim();
        if (text.Length == 0)
            return null;

        // Case 1: a JSON object — either a wrapper around the array, or a single candidate.
        if (text[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                foreach (var key in new[] { "memories", "items", "candidates", "results" })
                {
                    if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        return arr.Deserialize<List<CandidateDto>>(Json);
                }
                // A lone object that looks like one candidate.
                var single = root.Deserialize<CandidateDto>(Json);
                return single is null ? null : new List<CandidateDto> { single };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Case 2: a JSON array, possibly wrapped in prose. Extract it with a balanced-bracket scan
        // that respects string literals — never the naive "first [ to last ]" slice, which breaks
        // on a ] inside a string value or trailing commentary.
        var array = ExtractBalancedArray(text);
        return array is null ? null : TryDeserializeArray(array);
    }

    private static List<CandidateDto>? TryDeserializeArray(string json)
    {
        try { return JsonSerializer.Deserialize<List<CandidateDto>>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Returns the first complete top-level JSON array in <paramref name="s"/>, or null.</summary>
    private static string? ExtractBalancedArray(string s)
    {
        var start = s.IndexOf('[');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '[': depth++; break;
                case ']':
                    depth--;
                    if (depth == 0) return s[start..(i + 1)];
                    break;
            }
        }
        return null; // unbalanced → treat as unparseable
    }

    /// <summary>Strips a leading/trailing markdown code fence (```json … ```), if present.</summary>
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

    private static MemoryKind ParseKind(string? kind)
        => string.Equals(kind, "episodic", StringComparison.OrdinalIgnoreCase)
            ? MemoryKind.Episodic
            : MemoryKind.Semantic;

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static double Clamp(double? value, double fallback)
        => value is null ? fallback : Math.Clamp(value.Value, 0.0, 1.0);

    private const string SystemPrompt =
        "You extract durable memories from a conversation. Return ONLY a JSON array. Each item: " +
        "{\"kind\":\"semantic\"|\"episodic\", \"subject\":string?, \"predicate\":string?, \"value\":string?, " +
        "\"content\":string, \"validity\":\"Current\"|\"Temporary\"|\"Historical\"?, " +
        "\"episodeStatus\":\"Occurred\"|\"Planned\"|\"InProgress\"|\"Resolved\"?, \"relatedProject\":string?, " +
        "\"importance\":0..1, \"confidence\":0..1, \"excerpt\":\"the exact user words that support this\"}. " +
        "Only include things the user actually stated. Do not invent. If nothing is worth remembering, return [].";

    private sealed record CandidateDto(
        string? Kind, string? Subject, string? Predicate, string? Value, string Content,
        string? Validity, string? EpisodeStatus, string? RelatedProject,
        double? Importance, double? Confidence, string? Excerpt);
}
