using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Builds a warm, low-pressure opener from what the companion remembers — open loops first (the
/// things you meant to get back to), then recent projects. Deterministic and offline: the openers
/// are real, never invented, and the message always makes clear you can ignore them and just talk.
/// </summary>
public sealed class Greeter : IGreeter
{
    private const int MaxOpeners = 3;

    // Below this, a "gap" isn't worth acknowledging — you were basically still here.
    private static readonly TimeSpan MinGapToAcknowledge = TimeSpan.FromMinutes(30);

    // A companion commitment stops being surfaced after this, so it never nags indefinitely.
    private static readonly TimeSpan CommitmentSurfaceWindow = TimeSpan.FromDays(14);

    private readonly IProjectStore _projects;
    private readonly IMemoryStore _memories;
    private readonly IConversationStore _conversations;
    private readonly IRelationshipTracker _relationship;
    private readonly IEmotionStore _emotions;
    private readonly IReflectionStore _reflections;
    private readonly IAnticipationStore _anticipations;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;

    public Greeter(
        IProjectStore projects,
        IMemoryStore memories,
        IConversationStore conversations,
        IRelationshipTracker relationship,
        IEmotionStore emotions,
        IReflectionStore reflections,
        IAnticipationStore anticipations,
        IOptions<CompanionOptions> options,
        TimeProvider clock)
    {
        _projects = projects;
        _memories = memories;
        _conversations = conversations;
        _relationship = relationship;
        _emotions = emotions;
        _reflections = reflections;
        _anticipations = anticipations;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
    {
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var projects = (await _projects.GetProjectsAsync(userId, ct))
            .OrderByDescending(p => p.LastActivityAt)
            .ToList();
        var memories = await _memories.GetRetrievalCandidatesAsync(userId, ct);

        var now = _clock.GetUtcNow();

        // How long since we last talked? Only surfaced when it's a real gap.
        var lastSeen = await _conversations.GetLastMessageAtAsync(userId, ct);
        string? timeContext = null;
        if (lastSeen is { } seen)
        {
            var gap = now - seen;
            if (gap >= MinGapToAcknowledge)
                timeContext = RelativeTime.Describe(gap);
        }

        // A companion commitment is a promise the companion itself made ("I'll check in tomorrow").
        // Surface the freshest one — but let it expire so it never nags indefinitely.
        var commitments = openLoops
            .Where(l => string.Equals(l.Owner, "companion", StringComparison.OrdinalIgnoreCase)
                && now - l.CreatedAt <= CommitmentSurfaceWindow)
            .OrderByDescending(l => l.CreatedAt)
            .ToList();
        var userLoops = openLoops
            .Where(l => !string.Equals(l.Owner, "companion", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // How things have been feeling — so the opener can lead with care or shared good spirits
        // rather than jumping straight to tasks.
        var mood = await _relationship.BuildAsync(userId, ct);

        var openers = new List<string>();

        // A gentle, mood-aware opener comes first when the recent tone is notable — presence before
        // to-do list. Never presumes the cause; always an invitation, never a demand.
        var (moodOpener, surfacedTopic) = MoodOpener(mood);
        if (moodOpener is not null)
            openers.Add(moodOpener);

        // Asking about a topic closes the loop on it: we've raised it once, so it won't be brought up
        // again next session (the user can always return to it themselves).
        if (surfacedTopic is not null)
            await _emotions.MarkTopicFollowedUpAsync(userId, surfacedTopic, ct);

        // Dated events she's holding: encouragement on/just before the day, a follow-up once it's
        // passed. Each voiced at most once — surfacing marks the arc forward, same rule as the
        // mood follow-up above.
        var today = _clock.GetLocalNow().Date;
        foreach (var a in await _anticipations.GetOpenAsync(userId, ct))
        {
            if (openers.Count >= MaxOpeners)
                break;

            if (a.EventAt.Date < today && a.IsOpen)
            {
                openers.Add($"How did {a.Description} go?");
                await _anticipations.MarkFollowedUpAsync(userId, a.Id, now, ct);
            }
            else if (a.Status == AnticipationStatus.Pending
                && (a.EventAt.Date == today || a.EventAt.Date == today.AddDays(1)))
            {
                var when = a.EventAt.Date == today ? "today" : "tomorrow";
                openers.Add($"Good luck with {a.Description} {when} — I'll be thinking of you.");
                await _anticipations.MarkEncouragedAsync(userId, a.Id, now, ct);
            }
        }

        // Follow up on what the companion itself promised (its own initiative).
        foreach (var c in commitments.Take(1))
        {
            if (openers.Count >= MaxOpeners)
                break;
            openers.Add($"Last time I said I'd {LowerFirst(c.Description.TrimEnd('.'))} — want to pick that up?");
        }

        // A curiosity minted while the user was away — the between-session monologue reaching the
        // greeting. Voicing it here consumes it: raised once, then let go, exactly like the mood
        // follow-up above. The cooldown keeps a greeting and the following turns from each asking.
        var curiosity = await _reflections.GetNextToVoiceAsync(
            userId, now, TimeSpan.FromHours(_options.CuriosityCooldownHours), ct);
        if (curiosity is not null && openers.Count < MaxOpeners)
        {
            openers.Add($"Something I found myself wondering while you were away: {curiosity.Question}");
            await _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct);
        }

        // Then the user's own unfinished business.
        foreach (var loop in userLoops.Take(MaxOpeners - openers.Count))
            openers.Add($"Pick up where we left off — {LowerFirst(loop.Description.TrimEnd('.'))}?");

        // Then recent projects that aren't already covered by a user loop above.
        var loopProjectIds = userLoops.Select(l => l.ProjectId).ToHashSet();
        foreach (var project in projects.Where(p => !loopProjectIds.Contains(p.Id)).Take(MaxOpeners - openers.Count))
            openers.Add($"How's {project.Name} going?");

        // A gentle catch-all when there's some history but nothing actionable surfaced.
        if (openers.Count == 0 && memories.Count > 0)
            openers.Add("Ask me what I remember about you.");

        // A prior message means we've talked before, even if nothing durable was remembered.
        var hasHistory = lastSeen is not null || openLoops.Count > 0 || projects.Count > 0 || memories.Count > 0;

        // The message is the lead plus, when one exists, the first typed opener woven in as
        // her actual opening thought. Every value here comes from typed state - the clock,
        // stored anticipations, her own commitments, held curiosities, real loops. Nothing is
        // invented, and there is no starter menu: the chips it advertised no longer exist.
        string message;
        if (hasHistory)
        {
            var lead = timeContext is null
                ? "Hey — good to see you."
                : $"It's been {timeContext}. Good to see you back.";
            message = openers.Count > 0 ? lead + " " + openers[0] : lead;
        }
        else
        {
            message = "Hey — we haven't talked before, so there's nothing to catch up on yet. "
                + "Tell me anything — what you're working on, something on your mind — or ask "
                + "\"what can you do?\"";
        }

        return new Greeting { Message = message, TimeContext = timeContext, Openers = openers };
    }

    /// <summary>
    /// A low-pressure opener that acknowledges the recent emotional tone, plus the topic it surfaced
    /// (so the caller can close that loop). Both null when nothing is notable. Care first for a low
    /// stretch; warmth for good spirits; encouragement when climbing back up. Never presumes why.
    /// </summary>
    private static (string? Opener, string? SurfacedTopic) MoodOpener(RelationshipSnapshot mood)
    {
        if (!mood.HasHistory)
            return (null, null);

        var emotion = string.IsNullOrWhiteSpace(mood.RecentEmotion) ? null : mood.RecentEmotion;
        var topic = string.IsNullOrWhiteSpace(mood.RecentTopic) ? null : mood.RecentTopic;

        if (mood.RecentMood is Sentiment.Negative or Sentiment.VeryNegative)
        {
            // Tied to a subject → follow up on that specific thing ("how'd the interview go?").
            if (topic is not null)
            {
                var how = emotion is null ? "concerned" : emotion;
                return ($"Last time you seemed {how} about {topic} — how's that going?", topic);
            }

            var vibe = emotion is null ? "a bit low" : emotion;
            return ($"You seemed {vibe} last time — I'm here if you want to talk about it.", null);
        }

        if (mood.Trend == MoodTrend.Improving && mood.AverageValence < 0.2)
            return ("Last stretch felt rough — hope things have been looking up.", null);

        if (mood.RecentMood is Sentiment.Positive or Sentiment.VeryPositive)
        {
            if (topic is not null)
                return ($"How's {topic}? You seemed {emotion ?? "upbeat"} about it last time.", topic);
            return ("You were in good spirits last time — hope that's still going.", null);
        }

        return (null, null);
    }

    private static string LowerFirst(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
