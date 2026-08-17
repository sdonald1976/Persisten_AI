using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Text;
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

        // Decoded against a schema, not merely asked for JSON: the fields the pipeline makes
        // decisions on are enums, so the model cannot invent a fresh predicate per phrasing. See
        // ExtractionSchema.
        var raw = (await _chat.CompleteAsync(
            SystemPrompt, transcript.ToString(), ExtractionSchema.Format, ct: ct)).Text;

        // Bound the untrusted body before parsing so a runaway response can't be a problem here.
        if (raw.Length > MaxRawChars)
            raw = raw[..MaxRawChars];

        var dtos = TryParse(raw);
        if (dtos is null)
        {
            _logger.LogWarning("LLM extractor returned unparseable/invalid output; no candidates proposed.");
            return Array.Empty<MemoryCandidate>();
        }

        // An empty object is not an empty result, and the difference is the whole ballgame. Asked
        // for an array and answering "{}", the model has not judged that there is nothing worth
        // remembering — it has failed to engage with the task at all. That parses cleanly, produces
        // no candidates, and until now said nothing, which is how a companion whose entire purpose
        // is durable memory stored not one single fact across weeks of conversation while every log
        // line looked ordinary. A model too small for this prompt does exactly this, every time.
        //
        // "[]" is the honest empty answer and stays silent; this is the dishonest one.
        if (dtos.Count > 0 && dtos.All(d => string.IsNullOrWhiteSpace(d.Content)))
        {
            _logger.LogWarning(
                "LLM extractor produced no usable candidates: it was asked for a list and returned " +
                "{Raw}. NOTHING will be remembered from this turn. The usual cause is an extraction " +
                "model too small for the task — check the Models:Extraction role.",
                StripFence(raw).Trim());
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
                ProposedReplacement = dto.Replaces ?? false,
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

    /// <summary>Minimum share of an excerpt's words that must appear in a user message to count it as support.</summary>
    private const double EvidenceOverlapThreshold = 0.6;

    /// <summary>
    /// Verifies the model-supplied excerpt against the real user messages and returns grounded
    /// evidence, or empty if nothing supports it (no fabrication). An exact quote matches directly;
    /// but models routinely paraphrase the "excerpt", so a near-quote also counts when it overlaps
    /// strongly with a real user message — the evidence still cites that real message, we just don't
    /// drop a genuine memory over wording. A truly unrelated excerpt (low overlap) is still rejected.
    /// </summary>
    private static List<CandidateEvidence> ResolveEvidence(string? excerpt, List<Message> userMessages)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
            return new List<CandidateEvidence>();

        // Fast path: an exact (case-insensitive) quote.
        var exact = userMessages.FirstOrDefault(m =>
            m.Content.Contains(excerpt, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new List<CandidateEvidence> { new(exact.Id, excerpt.Trim()) };

        // Paraphrase path: cite the user message the excerpt overlaps most, if it's strong enough.
        var excerptTokens = new HashSet<string>(Tokenizer.Tokenize(excerpt));
        if (excerptTokens.Count == 0)
            return new List<CandidateEvidence>();

        Message? best = null;
        var bestScore = 0.0;
        foreach (var m in userMessages)
        {
            var messageTokens = new HashSet<string>(Tokenizer.Tokenize(m.Content));
            if (messageTokens.Count == 0)
                continue;
            var overlap = excerptTokens.Count(messageTokens.Contains) / (double)excerptTokens.Count;
            if (overlap > bestScore)
            {
                bestScore = overlap;
                best = m;
            }
        }

        return best is not null && bestScore >= EvidenceOverlapThreshold
            ? new List<CandidateEvidence> { new(best.Id, excerpt.Trim()) }
            : new List<CandidateEvidence>();
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

    private static string SystemPrompt => Companion.Core.Services.Prompts.Get("extraction.system");

    private sealed record CandidateDto(
        string? Kind, string? Subject, string? Predicate, string? Value, string Content,
        string? Validity, string? EpisodeStatus, string? RelatedProject,
        double? Importance, double? Confidence, string? Excerpt, bool? Replaces);
}
