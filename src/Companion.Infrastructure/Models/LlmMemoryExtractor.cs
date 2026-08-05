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

        var raw = await _chat.CompleteAsync(SystemPrompt, transcript.ToString(), ct);

        var dtos = TryParse(raw);
        if (dtos is null)
        {
            _logger.LogWarning("LLM extractor returned unparseable output; no candidates proposed.");
            return Array.Empty<MemoryCandidate>();
        }

        var candidates = new List<MemoryCandidate>();
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Content))
                continue;

            var evidence = ResolveEvidence(dto.Excerpt, userMessages);
            if (evidence.Count == 0)
                continue; // no traceable source → drop; the pipeline would reject it anyway

            candidates.Add(new MemoryCandidate
            {
                Kind = ParseKind(dto.Kind),
                Subject = dto.Subject,
                Predicate = dto.Predicate,
                Value = dto.Value,
                Content = dto.Content.Trim(),
                Validity = ParseEnum(dto.Validity, Validity.Current),
                EpisodeStatus = ParseEnum(dto.EpisodeStatus, EpisodeStatus.Occurred),
                RelatedProject = dto.RelatedProject,
                Importance = Clamp(dto.Importance, 0.5),
                ProposedConfidence = Clamp(dto.Confidence, 0.5),
                Evidence = evidence,
            });
        }

        return candidates;
    }

    private static List<CandidateEvidence> ResolveEvidence(string? excerpt, List<Message> userMessages)
    {
        if (!string.IsNullOrWhiteSpace(excerpt))
        {
            var match = userMessages.FirstOrDefault(m =>
                m.Content.Contains(excerpt, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return new List<CandidateEvidence> { new(match.Id, excerpt.Trim()) };
        }

        // Fall back to citing the most recent user message so provenance is never lost.
        var last = userMessages[^1];
        return new List<CandidateEvidence> { new(last.Id, last.Content) };
    }

    private static List<CandidateDto>? TryParse(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<CandidateDto>>(raw[start..(end + 1)], Json);
        }
        catch (JsonException)
        {
            return null;
        }
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
