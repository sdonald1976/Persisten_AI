using System.Text;
using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Text;
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
    private const int PriorMusings = 3;
    private const int RecentSignals = 5;

    /// <summary>Most of her own experiences one pass will read. Bounds the prompt.</summary>
    private const int MaxExperiences = 40;

    /// <summary>
    /// How much has to have happened to her for a pass to be worth running with nothing said. Set
    /// above a couple of idle wanders: a day in which she crossed one room is not a day worth
    /// writing about, and a diary that records every doorway becomes noise she reads back forever.
    /// </summary>
    private const int MinExperiencesAlone = 6;

    // Per-pass caps on what reflection may persist beyond the diary itself.
    private const int MaxSharedMoments = 2;
    private const int MaxPreferenceSignals = 3;
    private const int MaxSharedSummaryChars = 300;

    /// <summary>Minimum share of an evidence excerpt's words that must appear in a real message.</summary>
    private const double EvidenceOverlapThreshold = 0.5;

    private readonly IConversationStore _conversations;
    private readonly IReflectionStore _reflections;
    private readonly IProjectStore _projects;
    private readonly IEmotionStore _emotions;
    private readonly IMemoryStore _memories;
    private readonly IPreferenceStore _preferences;
    private readonly IAttentionStore _attention;
    private readonly IMemoryAssociationStore _associations;
    private readonly IProcedureStore _procedures;
    private readonly ISharedPerspectiveStore _sharedPerspectives;
    private readonly IExperienceStore _experiences;
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
        IMemoryStore memories,
        IPreferenceStore preferences,
        IAttentionStore attention,
        IMemoryAssociationStore associations,
        IProcedureStore procedures,
        ISharedPerspectiveStore sharedPerspectives,
        IExperienceStore experiences,
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
        _memories = memories;
        _preferences = preferences;
        _attention = attention;
        _associations = associations;
        _procedures = procedures;
        _sharedPerspectives = sharedPerspectives;
        _experiences = experiences;
        _chat = chat;
        _embeddings = embeddings;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReflectionOutcome> ReflectAsync(string userId, CancellationToken ct = default)
    {
        if (!_options.EnableReflection)
            return ReflectionOutcome.Skipped(ReflectionSkipReason.Disabled);

        // Everything new since the last pass. The store already excludes private conversations.
        var latest = await _reflections.GetLatestAsync(userId, ct);
        var messages = await _conversations.GetRememberableMessagesSinceAsync(
            userId, latest?.CoveredThrough, _options.ReflectionMaxMessages, ct);

        // Her own experiences since the same watermark. This is the point of her having a world:
        // until now every thought she could have was derived from something the user said, which
        // is a closed loop with one source. A day she spent somewhere is material too.
        var experiences = await _experiences.GetSinceAsync(
            userId, latest?.CoveredThrough, MaxExperiences, ct);

        var newUserMessages = messages.Count(m => m.Role == MessageRole.User);
        var enoughSaid = newUserMessages >= _options.ReflectionMinNewMessages;
        var enoughHappened = experiences.Count >= MinExperiencesAlone;

        if (!enoughSaid && !enoughHappened)
        {
            _logger.LogDebug(
                "No reflection for {UserId}: {Messages} new user messages (need {MinMessages}) "
                + "and {Experiences} experiences (need {MinExperiences}).",
                userId, newUserMessages, _options.ReflectionMinNewMessages,
                experiences.Count, MinExperiencesAlone);
            return ReflectionOutcome.Skipped(ReflectionSkipReason.NotEnoughMaterial);
        }

        var held = await _reflections.GetOpenCuriositiesAsync(userId, ct);
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var signals = await _emotions.GetRecentSignalsAsync(userId, RecentSignals, ct);
        var priorMusings = await RelevantPriorMusingsAsync(userId, messages, ct);

        var now = _clock.GetUtcNow();
        var material = ComposeMaterial(messages, experiences, priorMusings, held, openLoops, signals, now);

        var raw = (await _chat.CompleteAsync(SystemPrompt, material, format: ResponseFormat.Json, ct: ct)).Text;
        if (raw.Length > MaxRawChars)
            raw = raw[..MaxRawChars];

        var dto = TryParse(raw);
        if (dto is null)
        {
            // A model failure is not a quiet day: store nothing so this material is retried.
            _logger.LogWarning("Reflection for {UserId} produced unparseable output; nothing stored.", userId);
            return ReflectionOutcome.Skipped(ReflectionSkipReason.ModelOutputUnusable);
        }

        var reflection = new Reflection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            // The watermark has to cover both sources, or whichever one it ignores is read again
            // forever. It also cannot assume there were any messages: a day she spent in her world
            // without being spoken to is exactly the case this step exists to make reflectable.
            CoveredThrough = Latest(messages, experiences, now),
            MessagesReflected = messages.Count,
        };

        var musing = Normalize(dto.Musing, MaxMusingChars);
        if (musing is not null)
        {
            reflection.Musing = musing;
            reflection.Embedding = await _embeddings.EmbedAsync(musing, ct);
            ApplyThread(reflection, dto, priorMusings);
        }

        var curiosities = SelectCuriosities(dto.Curiosities, held, userId, now);

        // Persisted even without a musing: the watermark advance IS the record of a quiet day.
        await _reflections.AddAsync(reflection, curiosities, ct);

        // Shared moments: evidence-verified episodes the user and companion had TOGETHER.
        var sharedMoments = await PersistSharedMomentsAsync(userId, dto.SharedMoments, messages, now, ct);

        // Preference signals: her own tastes, evolved gradually — never copied from the user.
        var preferences = await ApplyPreferenceSignalsAsync(userId, dto.Preferences, messages, now, ct);

        var attentionItems = await PersistAttentionCandidatesAsync(userId, dto.AttentionCandidates, messages, now, ct);
        var associations = await PersistAssociationCandidatesAsync(userId, dto.AssociationCandidates, messages, now, ct);
        var procedures = await PersistProcedureCandidatesAsync(userId, dto.ProcedureCandidates, messages, now, ct);
        var sharedPerspectives = await PersistSharedPerspectiveCandidatesAsync(
            userId, dto.SharedPerspectiveCandidates, messages, now, ct);

        // Curiosities the conversation answered close with satisfaction instead of silence.
        var satisfied = await MarkSettledAsync(userId, dto.Settled, held, ct);

        _logger.LogInformation(
            "Reflected for {UserId} over {Messages} messages: {Kind}, {Curiosities} new curiosities, " +
            "{Shared} shared moments, {Preferences} preference signals, {Satisfied} curiosities satisfied.",
            userId, messages.Count, musing is null ? "quiet day" : "musing written",
            curiosities.Count, sharedMoments.Count, preferences.Count, satisfied);

        return ReflectionOutcome.From(new ReflectionResult
        {
            Reflection = reflection,
            Curiosities = curiosities,
            SharedMoments = sharedMoments,
            Preferences = preferences,
            AttentionItems = attentionItems,
            Associations = associations,
            Procedures = procedures,
            SharedPerspectives = sharedPerspectives,
            SatisfiedCuriosities = satisfied,
        });
    }

    /// <summary>
    /// Persists shared moments as <see cref="MemoryOwner.Shared"/> episodic memories — but only
    /// with verified provenance: the cited words must actually appear in (or strongly overlap) a
    /// real message from the window. No evidence, no episode; reflection cannot invent history.
    /// </summary>
    private async Task<List<EpisodicMemory>> PersistSharedMomentsAsync(
        string userId, List<SharedMomentDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var persisted = new List<EpisodicMemory>();
        if (proposed is null)
            return persisted;

        foreach (var dto in proposed.Take(MaxSharedMoments))
        {
            var summary = Normalize(dto.Summary, MaxSharedSummaryChars);
            if (summary is null)
                continue;

            var source = ResolveEvidence(dto.Evidence, messages);
            if (source is null)
            {
                _logger.LogWarning(
                    "Rejected a shared moment for {UserId}: its evidence could not be verified.", userId);
                continue;
            }

            var episode = new EpisodicMemory
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Description = summary,
                Owner = MemoryOwner.Shared,
                EventTime = source.Timestamp,
                TimePrecision = TimePrecision.Day,
                MentionedAt = now,
                CreatedAt = now,
                EpisodeStatus = EpisodeStatus.Occurred,
                Importance = Math.Clamp(dto.Significance ?? 0.6, 0.0, 1.0),
                Confidence = 0.7, // derived by reflection, not a direct statement
                EmotionalSignificance = Normalize(dto.Tone, 60),
                Status = MemoryStatus.Active,
                Embedding = await _embeddings.EmbedAsync(summary, ct),
            };
            episode.Evidence.Add(new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                MemoryId = episode.Id,
                MemoryKind = MemoryKind.Episodic,
                MessageId = source.Id,
                Excerpt = Normalize(dto.Evidence, 200) ?? summary,
                Weight = 1.0,
            });

            await _memories.AddEpisodicAsync(episode, ct);
            await _memories.AddRevisionAsync(userId, new MemoryRevision
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                MemoryId = episode.Id,
                MemoryKind = MemoryKind.Episodic,
                Kind = RevisionKind.Created,
                Timestamp = now,
                Actor = "reflection",
                Note = "Shared moment noticed during between-session reflection.",
                After = summary,
            }, ct);

            persisted.Add(episode);
        }

        return persisted;
    }

    /// <summary>Applies validated preference signals through the store's evolution rules.</summary>
    private async Task<List<CompanionPreference>> ApplyPreferenceSignalsAsync(
        string userId, List<PreferenceDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var applied = new List<CompanionPreference>();
        if (proposed is null)
            return applied;

        foreach (var dto in proposed.Take(MaxPreferenceSignals))
        {
            if (!IsCompanionOwner(dto.Owner))
            {
                _logger.LogWarning("Rejected a companion preference for {UserId}: owner was not Companion.", userId);
                continue;
            }

            var source = ResolveEvidence(dto.Evidence, messages);
            if (source is null || source.Role != MessageRole.Assistant)
            {
                _logger.LogWarning(
                    "Rejected a companion preference for {UserId}: evidence was missing or not assistant-owned.",
                    userId);
                continue;
            }

            var subject = Normalize(dto.Subject, 200);
            if (subject is null)
                continue;

            var target = PreferenceMath.TargetAffinity(dto.Feeling, dto.Strength);
            var reason = Normalize(dto.Reason, 400);
            var embedding = await _embeddings.EmbedAsync(
                reason is null ? subject : $"{subject} — {reason}", ct);

            applied.Add(await _preferences.ApplySignalAsync(
                userId, subject, target, reason, embedding, now, ct));
        }

        return applied;
    }

    private static bool IsCompanionOwner(string? owner)
        => string.Equals(owner?.Trim(), "Companion", StringComparison.OrdinalIgnoreCase);

    private async Task<List<AttentionItem>> PersistAttentionCandidatesAsync(
        string userId, List<AttentionCandidateDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var persisted = new List<AttentionItem>();
        if (proposed is null)
            return persisted;

        foreach (var dto in proposed.Take(_options.MaxAttentionItems))
        {
            var summary = Normalize(dto.Summary, 500);
            var subject = Normalize(dto.Subject, 160);
            var source = ResolveEvidence(dto.Evidence, messages);
            if (summary is null || subject is null || source is null)
                continue;

            var item = new AttentionItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Subject = subject,
                Summary = summary,
                SourceType = AttentionSourceType.Reflection,
                SourceId = source.Id.ToString(),
                Owner = MemoryOwner.Shared,
                Strength = Math.Clamp(dto.Strength ?? 0.45, 0, 1),
                CreatedAt = now,
                LastActivatedAt = now,
                ExpiresAt = now.AddDays(_options.AttentionTtlDays),
                Status = AttentionStatus.Active,
            };
            await _attention.UpsertAsync(item, ct);
            persisted.Add(item);
        }
        return persisted;
    }

    private async Task<List<MemoryAssociation>> PersistAssociationCandidatesAsync(
        string userId, List<AssociationCandidateDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var persisted = new List<MemoryAssociation>();
        if (proposed is null)
            return persisted;

        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);
        foreach (var dto in proposed.Take(3))
        {
            if (ResolveEvidence(dto.Evidence, messages) is null)
                continue;
            var source = BestMemoryMatch(memories, dto.SourceMemory);
            var target = BestMemoryMatch(memories, dto.TargetMemory);
            if (source is null || target is null)
                continue;
            var association = await _associations.AddValidatedAsync(new MemoryAssociation
            {
                UserId = userId,
                SourceMemoryId = source.Id,
                TargetMemoryId = target.Id,
                AssociationType = ParseAssociationType(dto.AssociationType),
                Strength = Math.Clamp(dto.Strength ?? 0.65, 0, 1),
                Evidence = Normalize(dto.Evidence, 500) ?? "reflection evidence",
                CreatedAt = now,
                LastReinforcedAt = now,
            }, ct);
            if (association is not null)
                persisted.Add(association);
        }
        return persisted;
    }

    private async Task<List<Procedure>> PersistProcedureCandidatesAsync(
        string userId, List<ProcedureCandidateDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var persisted = new List<Procedure>();
        if (proposed is null)
            return persisted;

        foreach (var dto in proposed.Take(2))
        {
            var source = ResolveEvidence(dto.Evidence, messages);
            if (source is null || source.Role != MessageRole.User)
                continue;
            var text = Normalize(dto.Text, 1000) ?? source.Content;
            if (!LooksLikeExplicitProcedureTeaching(text))
                continue;
            var teachingMessage = new Message
            {
                Id = source.Id,
                ConversationId = source.ConversationId,
                UserId = source.UserId,
                Role = source.Role,
                Content = text,
                Timestamp = source.Timestamp,
                ReplyToId = source.ReplyToId,
                TokenCount = source.TokenCount,
            };
            var procedure = await _procedures.AddOrUpdateFromTeachingAsync(userId, source.ConversationId, teachingMessage, now, ct);
            if (procedure is not null)
                persisted.Add(procedure);
        }
        return persisted;
    }

    private async Task<List<SharedExperiencePerspective>> PersistSharedPerspectiveCandidatesAsync(
        string userId, List<SharedPerspectiveCandidateDto>? proposed, IReadOnlyList<Message> messages,
        DateTimeOffset now, CancellationToken ct)
    {
        var persisted = new List<SharedExperiencePerspective>();
        if (proposed is null)
            return persisted;

        var shared = (await _memories.GetRetrievableMemoriesAsync(userId, ct))
            .OfType<EpisodicMemory>()
            .Where(m => m.Owner == MemoryOwner.Shared)
            .ToList();
        foreach (var dto in proposed.Take(3))
        {
            var summary = Normalize(dto.Summary, 500);
            if (summary is null || ResolveEvidence(dto.Evidence, messages) is null)
                continue;
            var experience = BestSharedExperienceMatch(shared, dto.Experience);
            if (experience is null)
                continue;
            var owner = ParseOwner(dto.Owner);
            if (owner is null || owner == MemoryOwner.Shared)
                continue;

            var perspective = await _sharedPerspectives.AddValidatedAsync(new SharedExperiencePerspective
            {
                UserId = userId,
                ExperienceId = experience.Id,
                Owner = owner.Value,
                Summary = summary,
                Confidence = Math.Clamp(dto.Confidence ?? 0.6, 0, 1),
                Evidence = Normalize(dto.Evidence, 500) ?? summary,
                CreatedAt = now,
            }, ct);
            if (perspective is not null)
                persisted.Add(perspective);
        }
        return persisted;
    }

    /// <summary>Matches "settled" notes from the model against held curiosities and closes them.</summary>
    private async Task<int> MarkSettledAsync(
        string userId, List<string>? settled, IReadOnlyList<Curiosity> held, CancellationToken ct)
    {
        if (settled is null || settled.Count == 0)
            return 0;

        var count = 0;
        foreach (var note in settled.Select(s => Normalize(s, 300)).Where(s => s is not null))
        {
            var match = held.FirstOrDefault(c =>
                (c.About is not null && (note!.Contains(c.About, StringComparison.OrdinalIgnoreCase)
                    || c.About.Contains(note!, StringComparison.OrdinalIgnoreCase)))
                || note!.Contains(c.Question, StringComparison.OrdinalIgnoreCase)
                || c.Question.Contains(note!, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                continue;

            await _reflections.MarkSatisfiedAsync(userId, match.Id, ct);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Finds the real message the cited evidence came from: an exact (case-insensitive) quote, or
    /// the strongest token-overlap match above the threshold. Null = unverifiable = not persisted.
    /// </summary>
    /// <summary>
    /// The furthest point this pass has accounted for, across both sources. Falls back to now when
    /// neither has anything, which the gate above makes impossible but which would otherwise be a
    /// crash rather than a quiet day.
    /// </summary>
    private static DateTimeOffset Latest(
        IReadOnlyList<Message> messages, IReadOnlyList<Experience> experiences, DateTimeOffset now)
    {
        var lastSaid = messages.Count > 0 ? messages[^1].Timestamp : (DateTimeOffset?)null;
        var lastHappened = experiences.Count > 0 ? experiences[^1].At : (DateTimeOffset?)null;

        if (lastSaid is null && lastHappened is null)
            return now;
        if (lastSaid is null)
            return lastHappened!.Value;
        if (lastHappened is null)
            return lastSaid.Value;

        return lastSaid.Value > lastHappened.Value ? lastSaid.Value : lastHappened.Value;
    }

    private static Message? ResolveEvidence(string? excerpt, IReadOnlyList<Message> messages)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
            return null;

        var exact = messages.FirstOrDefault(m =>
            m.Content.Contains(excerpt, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var excerptTokens = new HashSet<string>(Tokenizer.Tokenize(excerpt));
        if (excerptTokens.Count == 0)
            return null;

        Message? best = null;
        var bestScore = 0.0;
        foreach (var m in messages)
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

        return bestScore >= EvidenceOverlapThreshold ? best : null;
    }

    private static IMemory? BestMemoryMatch(IReadOnlyList<IMemory> memories, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return memories
            .Select(m => (Memory: m, Score: ScoreMath.KeywordOverlap(text, m.Content)))
            .Where(x => x.Score >= 0.1 || x.Memory.Content.Contains(text, StringComparison.OrdinalIgnoreCase)
                || text.Contains(x.Memory.Content, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Memory)
            .FirstOrDefault();
    }

    private static EpisodicMemory? BestSharedExperienceMatch(IReadOnlyList<EpisodicMemory> shared, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return shared
            .Select(m => (Memory: m, Score: ScoreMath.KeywordOverlap(text, m.Description)))
            .Where(x => x.Score >= 0.1 || x.Memory.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
                || text.Contains(x.Memory.Description, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Memory)
            .FirstOrDefault();
    }

    private static MemoryAssociationType ParseAssociationType(string? type)
        => Enum.TryParse<MemoryAssociationType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : MemoryAssociationType.TopicRelated;

    private static MemoryOwner? ParseOwner(string? owner)
        => Enum.TryParse<MemoryOwner>(owner, ignoreCase: true, out var parsed) ? parsed : null;

    private static bool LooksLikeExplicitProcedureTeaching(string text)
        => text.Contains("this is how i", StringComparison.OrdinalIgnoreCase)
           || text.Contains("this is our", StringComparison.OrdinalIgnoreCase)
           || text.Contains("remember this process", StringComparison.OrdinalIgnoreCase)
           || text.Contains("learn this workflow", StringComparison.OrdinalIgnoreCase)
           || text.Contains("whenever i ask for", StringComparison.OrdinalIgnoreCase);

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

    // ---- continuity of thought ----

    /// <summary>
    /// Places this musing in a train of thought. The model proposes which earlier thought it
    /// develops (by the short id it was shown); deterministic code decides whether to believe it:
    /// the id must match a musing that was actually offered THIS pass, which makes it impossible
    /// to graft onto an arbitrary or invented row. An unmatched or absent claim simply starts a
    /// new thread — the honest default, and the pre-threading behavior.
    /// </summary>
    private static void ApplyThread(
        Reflection reflection, ReflectionDto dto, IReadOnlyList<Reflection> priorMusings)
    {
        var claimed = dto.ContinuesThought?.Trim();
        var parent = string.IsNullOrEmpty(claimed)
            ? null
            : priorMusings.FirstOrDefault(r =>
                ShortId(r.Id).Equals(claimed, StringComparison.OrdinalIgnoreCase)
                || r.Id.ToString().Equals(claimed, StringComparison.OrdinalIgnoreCase));

        if (parent is null)
        {
            // A new train of thought: it is its own root.
            reflection.ThreadId = reflection.Id;
            reflection.ContinuesReflectionId = null;
        }
        else
        {
            reflection.ContinuesReflectionId = parent.Id;
            // Inherit the parent's thread — including when the parent predates threading and has
            // no id of its own, in which case the parent becomes the root.
            reflection.ThreadId = parent.ThreadId == Guid.Empty ? parent.Id : parent.ThreadId;
        }

        // Settling only means something for a thought that continues one; a brand-new thought
        // declaring itself finished is just a thought.
        reflection.ThreadSettled = dto.ThoughtSettled == true && parent is not null;
    }

    /// <summary>Short, prompt-friendly handle for a reflection (the model echoes it back).</summary>
    private static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>How far back a dormant thread can still be picked up.</summary>
    private const int PriorMusingLookback = 40;

    /// <summary>
    /// The past thoughts worth continuing THIS pass. Recency alone made each reflection nearly
    /// independent: a thread she was developing three cycles ago was invisible the moment two
    /// newer thoughts existed, so she restarted instead of continuing. This keeps the latest
    /// thought (the conversation's current shape) and adds the most RELEVANT older ones — judged
    /// by similarity between the new material and each musing — so a dormant thread resurfaces
    /// when its subject comes back around.
    /// </summary>
    private async Task<IReadOnlyList<Reflection>> RelevantPriorMusingsAsync(
        string userId, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var recent = (await _reflections.GetRecentAsync(userId, PriorMusingLookback, ct))
            .Where(r => r.HasMusing)
            .ToList();

        // A thread she has settled is finished thinking about. Excluding the whole thread (not
        // just the settling entry) is what stops a resolved thought being resumed forever —
        // the difference between a train of thought and rumination.
        var settled = recent
            .Where(r => r.ThreadSettled && r.ThreadId != Guid.Empty)
            .Select(r => r.ThreadId)
            .ToHashSet();
        var candidates = recent.Where(r => !settled.Contains(r.ThreadId)).ToList();
        if (candidates.Count <= PriorMusings)
            return candidates;

        // The newest thought always comes along: it is the thread she was most recently on.
        var latest = candidates.OrderByDescending(r => r.CreatedAt).First();
        var rest = candidates.Where(r => r.Id != latest.Id).ToList();

        float[] materialEmbedding;
        try
        {
            var gist = string.Join('\n', messages
                .Where(m => m.Role == MessageRole.User)
                .TakeLast(RelevanceGistMessages)
                .Select(m => m.Content));
            materialEmbedding = string.IsNullOrWhiteSpace(gist)
                ? Array.Empty<float>()
                : await _embeddings.EmbedAsync(gist, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No embeddings available — fall back to the old recency behavior rather than fail.
            _logger.LogDebug(ex, "Embedding unavailable for prior-musing selection; using recency.");
            materialEmbedding = Array.Empty<float>();
        }

        var chosen = materialEmbedding.Length == 0
            ? rest.OrderByDescending(r => r.CreatedAt).Take(PriorMusings - 1)
            : rest.Select(r => (Reflection: r, Score: ScoreMath.Cosine(materialEmbedding, r.Embedding)))
                .Where(x => x.Score >= PriorMusingRelevanceFloor)
                .OrderByDescending(x => x.Score)
                .Take(PriorMusings - 1)
                .Select(x => x.Reflection);

        return chosen.Append(latest).OrderBy(r => r.CreatedAt).ToList();
    }

    /// <summary>How many recent user messages summarize what this pass is about.</summary>
    private const int RelevanceGistMessages = 6;

    /// <summary>Minimum similarity for an older thought to be worth resuming.</summary>
    private const double PriorMusingRelevanceFloor = 0.25;

    // ---- prompt material ----

    private static string ComposeMaterial(
        IReadOnlyList<Message> messages,
        IReadOnlyList<Experience> experiences,
        IReadOnlyList<Reflection> priorMusings,
        IReadOnlyList<Curiosity> held,
        IReadOnlyList<OpenLoop> openLoops,
        IReadOnlyList<EmotionalSignal> signals,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();

        if (priorMusings.Count > 0)
        {
            sb.AppendLine("## Your earlier thoughts (continue one by id, or start a new one)");
            foreach (var r in priorMusings.OrderBy(r => r.CreatedAt))
            {
                sb.AppendLine(
                    $"- id={ShortId(r.Id)} [{RelativeTime.Describe(now - r.CreatedAt)} ago] {r.Musing}");
            }
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

        // Her own day, kept separate from the conversation so the two can never be confused. These
        // happened to HER; nothing here is a fact about the user, and the heading says so because
        // the model is the thing that would otherwise blur it.
        if (experiences.Count > 0)
        {
            sb.AppendLine("## Your own day (things you did, not things they told you)");
            foreach (var e in experiences)
                sb.AppendLine($"- {e.At.ToLocalTime():HH:mm} {e.Text}");
            sb.AppendLine();
        }

        sb.AppendLine("## What happened since you last thought things over");
        if (messages.Count == 0)
            sb.AppendLine("(You weren't spoken to. Whatever you have to think about is your own.)");
        foreach (var m in messages)
            sb.AppendLine($"{m.Role}: {m.Content}");

        return sb.ToString();
    }

    private static string SystemPrompt => Prompts.Get("reflection.system");

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

    private sealed record ReflectionDto(
        string? Musing, string? ContinuesThought, bool? ThoughtSettled,
        List<CuriosityDto>? Curiosities, List<SharedMomentDto>? SharedMoments,
        List<PreferenceDto>? Preferences,
        List<AttentionCandidateDto>? AttentionCandidates,
        List<AssociationCandidateDto>? AssociationCandidates,
        List<ProcedureCandidateDto>? ProcedureCandidates,
        List<SharedPerspectiveCandidateDto>? SharedPerspectiveCandidates,
        List<string>? Settled);
    private sealed record CuriosityDto(string? Question, string? About, string? Reason);
    private sealed record SharedMomentDto(string? Summary, string? Evidence, double? Significance, string? Tone);
    private sealed record PreferenceDto(
        string? Owner, string? Subject, string? Feeling, string? Strength, string? Reason, string? Evidence);
    private sealed record AttentionCandidateDto(string? Subject, string? Summary, double? Strength, string? Evidence);
    private sealed record AssociationCandidateDto(
        string? SourceMemory, string? TargetMemory, string? AssociationType, double? Strength, string? Evidence);
    private sealed record ProcedureCandidateDto(string? Text, string? Evidence);
    private sealed record SharedPerspectiveCandidateDto(
        string? Experience, string? Owner, string? Summary, double? Confidence, string? Evidence);
}
