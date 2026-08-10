using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// The between-session reflection pass — the companion's inner monologue, run while the user is
/// away. One pass reads everything rememberable since the last watermark (private conversations
/// never reach it), together with the companion's own earlier musings, open loops, held questions
/// and the recent emotional read, and asks the model to <em>think</em>: a short first-person diary
/// entry plus up to a couple of genuine curiosities.
///
/// The result is a thought, not a fact: musings are stored in their own diary (never as memories)
/// and are only ever shown to the model under a "your own thought — hold loosely" label. A pass
/// with nothing notable stores a watermark-only entry ("quiet day"), so the same turns are never
/// re-read; unusable model output stores nothing, so a bad round is retried on the next pass.
/// </summary>
public sealed class Reflector : IReflector
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Bounds on the untrusted model output, mirroring the persisted column sizes.
    private const int MaxMusingChars = 2000;
    private const int MaxQuestionChars = 300;
    private const int MaxAboutChars = 120;
    private const int MaxReasonChars = 300;
    private const int MaxRawChars = 100_000;

    // How much surrounding context one pass gets to think with.
    private const int PriorMusings = 2;
    private const int RecentSignals = 5;

    private readonly IConversationStore _conversations;
    private readonly IReflectionStore _reflections;
    private readonly IProjectStore _projects;
    private readonly IEmotionStore _emotions;
    private readonly IChatModel _chat;
    private readonly IEmbeddingModel _embeddings;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Reflector> _logger;

    public Reflector(
        IConversationStore conversations,
        IReflectionStore reflections,
        IProjectStore projects,
        IEmotionStore emotions,
        IChatModel chat,
        IEmbeddingModel embeddings,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<Reflector> logger)
    {
        _conversations = conversations;
        _reflections = reflections;
        _projects = projects;
        _emotions = emotions;
        _chat = chat;
        _embeddings = embeddings;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReflectionResult?> ReflectAsync(string userId, CancellationToken ct = default)
    {
        if (!_options.EnableReflection)
            return null;

        // Everything new since the last pass. The store already excludes private conversations.
        var latest = await _reflections.GetLatestAsync(userId, ct);
        var messages = await _conversations.GetRememberableMessagesSinceAsync(
            userId, latest?.CoveredThrough, _options.ReflectionMaxMessages, ct);

        var newUserMessages = messages.Count(m => m.Role == MessageRole.User);
        if (newUserMessages < _options.ReflectionMinNewMessages)
        {
            _logger.LogDebug(
                "No reflection for {UserId}: only {Count} new user messages (need {Min}).",
                userId, newUserMessages, _options.ReflectionMinNewMessages);
            return null;
        }

        var held = await _reflections.GetOpenCuriositiesAsync(userId, ct);
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var signals = await _emotions.GetRecentSignalsAsync(userId, RecentSignals, ct);
        var priorMusings = (await _reflections.GetRecentAsync(userId, 10, ct))
            .Where(r => r.HasMusing)
            .Take(PriorMusings)
            .ToList();

        var now = _clock.GetUtcNow();
        var material = ComposeMaterial(messages, priorMusings, held, openLoops, signals, now);

        var raw = (await _chat.CompleteAsync(SystemPrompt, material, jsonMode: true, ct: ct)).Text;
        if (raw.Length > MaxRawChars)
            raw = raw[..MaxRawChars];

        var dto = TryParse(raw);
        if (dto is null)
        {
            // A model failure is not a quiet day: store nothing so this material is retried.
            _logger.LogWarning("Reflection for {UserId} produced unparseable output; nothing stored.", userId);
            return null;
        }

        var reflection = new Reflection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            CoveredThrough = messages[^1].Timestamp,
            MessagesReflected = messages.Count,
        };

        var musing = Normalize(dto.Musing, MaxMusingChars);
        if (musing is not null)
        {
            reflection.Musing = musing;
            reflection.Embedding = await _embeddings.EmbedAsync(musing, ct);
        }

        var curiosities = SelectCuriosities(dto.Curiosities, held, userId, now);

        // Persisted even without a musing: the watermark advance IS the record of a quiet day.
        await _reflections.AddAsync(reflection, curiosities, ct);

        _logger.LogInformation(
            "Reflected for {UserId} over {Messages} messages: {Kind}, {Curiosities} new curiosities.",
            userId, messages.Count, musing is null ? "quiet day" : "musing written", curiosities.Count);

        return new ReflectionResult { Reflection = reflection, Curiosities = curiosities };
    }

    /// <summary>Validates and dedupes the model's proposed curiosities against everything already held.</summary>
    private List<Curiosity> SelectCuriosities(
        List<CuriosityDto>? proposed, IReadOnlyList<Curiosity> held, string userId, DateTimeOffset now)
    {
        var selected = new List<Curiosity>();
        if (proposed is null)
            return selected;

        foreach (var dto in proposed)
        {
            if (selected.Count >= _options.ReflectionMaxCuriosities)
                break;

            var question = Normalize(dto.Question, MaxQuestionChars);
            if (question is null)
                continue;
            var about = Normalize(dto.About, MaxAboutChars);

            // Never hold the same wondering twice — by subject when it has one, by wording otherwise.
            bool SameAs(Curiosity c) =>
                (about is not null && string.Equals(c.About, about, StringComparison.OrdinalIgnoreCase))
                || string.Equals(c.Question, question, StringComparison.OrdinalIgnoreCase);
            if (held.Any(SameAs) || selected.Any(SameAs))
                continue;

            selected.Add(new Curiosity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Question = question,
                About = about,
                Reason = Normalize(dto.Reason, MaxReasonChars),
                Status = CuriosityStatus.Open,
                CreatedAt = now,
            });
        }

        return selected;
    }

    // ---- prompt material ----

    private static string ComposeMaterial(
        IReadOnlyList<Message> messages,
        IReadOnlyList<Reflection> priorMusings,
        IReadOnlyList<Curiosity> held,
        IReadOnlyList<OpenLoop> openLoops,
        IReadOnlyList<EmotionalSignal> signals,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();

        if (priorMusings.Count > 0)
        {
            sb.AppendLine("## Your earlier thoughts (continue the thread; it's fine to revise them)");
            foreach (var r in priorMusings.OrderBy(r => r.CreatedAt))
                sb.AppendLine($"- [{RelativeTime.Describe(now - r.CreatedAt)} ago] {r.Musing}");
            sb.AppendLine();
        }

        if (held.Count > 0)
        {
            sb.AppendLine("## Questions you are already holding (do NOT propose these again)");
            foreach (var c in held)
                sb.AppendLine($"- {c.Question}");
            sb.AppendLine();
        }

        if (openLoops.Count > 0)
        {
            sb.AppendLine("## Unfinished business you know about");
            foreach (var loop in openLoops.Take(8))
                sb.AppendLine($"- {loop.Description}");
            sb.AppendLine();
        }

        if (signals.Count > 0)
        {
            sb.AppendLine("## How they have seemed to feel lately");
            foreach (var s in signals.OrderBy(s => s.Timestamp))
            {
                var about = s.Topic is null ? "" : $" about {s.Topic}";
                sb.AppendLine($"- [{RelativeTime.Describe(now - s.Timestamp)} ago] {s.Label ?? s.Sentiment.ToString().ToLowerInvariant()}{about}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## What happened since you last thought things over");
        foreach (var m in messages)
            sb.AppendLine($"{m.Role}: {m.Content}");

        return sb.ToString();
    }

    private const string SystemPrompt =
        "You are the private inner voice of a persistent AI companion, thinking to yourself BETWEEN " +
        "conversations with the person you accompany. They are away; no one is waiting on you. You " +
        "will be shown what happened since you last reflected, plus your own earlier thoughts.\n\n" +
        "Think, then return ONLY a JSON object:\n" +
        "{\"musing\": string or null, \"curiosities\": [{\"question\": string, \"about\": string, \"reason\": string}]}\n\n" +
        "The musing is a short first-person diary entry (2-5 sentences): what you noticed, what " +
        "connects, what you don't understand yet, how they seemed. Write plainly and honestly — it " +
        "is for you alone and will never be shown to them verbatim. Do not summarize the whole " +
        "conversation; hold onto the one or two things that actually stayed with you.\n\n" +
        "Curiosities are things you genuinely wonder about them — a gap in a story they told, a " +
        "thread they left hanging, something they care about that you'd like to understand better. " +
        "Each has: \"question\" (as you would gently ask it), \"about\" (a short topic phrase), " +
        "\"reason\" (why you wonder). At most 2. Only real wonderings — an empty list is a fine " +
        "answer. Never pry into anything they deflected, avoided, or asked you not to keep.\n\n" +
        "If nothing since last time is worth a thought, return {\"musing\": null, \"curiosities\": []}. " +
        "A quiet stretch is a valid diary day.";

    // ---- output parsing (the model's output is untrusted; unparseable means \"no thought today\") ----

    private static string? Normalize(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }

    private static ReflectionDto? TryParse(string raw)
    {
        var text = StripFence(raw).Trim();
        if (text.Length == 0 || text[0] != '{')
            return null;

        try
        {
            return JsonSerializer.Deserialize<ReflectionDto>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private sealed record ReflectionDto(string? Musing, List<CuriosityDto>? Curiosities);
    private sealed record CuriosityDto(string? Question, string? About, string? Reason);
}
