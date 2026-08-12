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
                openers.Add(Prompts.Format("greeter.opener.anticipation.after", ("description", a.Description)));
                await _anticipations.MarkFollowedUpAsync(userId, a.Id, now, ct);
            }
            else if (a.Status == AnticipationStatus.Pending
                && (a.EventAt.Date == today || a.EventAt.Date == today.AddDays(1)))
            {
                var when = a.EventAt.Date == today ? "today" : "tomorrow";
                openers.Add(Prompts.Format("greeter.opener.anticipation.upcoming",
                    ("description", a.Description), ("when", when)));
                await _anticipations.MarkEncouragedAsync(userId, a.Id, now, ct);
            }
        }

        // Follow up on what the companion itself promised (its own initiative).
        foreach (var c in commitments.Take(1))
        {
            if (openers.Count >= MaxOpeners)
                break;
            openers.Add(Prompts.Format("greeter.opener.commitment", ("commitment", LowerFirst(c.Description.TrimEnd('.')))));
        }

        // A curiosity minted while the user was away — the between-session monologue reaching the
        // greeting. Voicing it here consumes it: raised once, then let go, exactly like the mood
        // follow-up above. The cooldown keeps a greeting and the following turns from each asking.
        var curiosity = await _reflections.GetNextToVoiceAsync(
            userId, now, TimeSpan.FromHours(_options.CuriosityCooldownHours), ct);
        if (curiosity is not null && openers.Count < MaxOpeners)
        {
            openers.Add(Prompts.Format("greeter.opener.curiosity", ("question", curiosity.Question)));
            await _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct);
        }

        // Then the user's own unfinished business.
        foreach (var loop in userLoops.Take(MaxOpeners - openers.Count))
            openers.Add(Prompts.Format("greeter.opener.loop", ("description", LowerFirst(loop.Description.TrimEnd('.')))));

        // Then recent projects that aren't already covered by a user loop above.
        var loopProjectIds = userLoops.Select(l => l.ProjectId).ToHashSet();
        foreach (var project in projects.Where(p => !loopProjectIds.Contains(p.Id)).Take(MaxOpeners - openers.Count))
            openers.Add(Prompts.Format("greeter.opener.project", ("project", project.Name)));

        // A gentle catch-all when there's some history but nothing actionable surfaced.
        if (openers.Count == 0 && memories.Count > 0)
            openers.Add(Prompts.Get("greeter.opener.recall"));

        // A prior message means we've talked before, even if nothing durable was remembered.
        var hasHistory = lastSeen is not null || openLoops.Count > 0 || projects.Count > 0 || memories.Count > 0;

        string message;
        if (hasHistory)
        {
            var lead = timeContext is null
                ? Prompts.Get("greeter.lead.nogap")
                : Prompts.Format("greeter.lead.gap", ("gap", timeContext));
            message = lead + " " + Prompts.Get("greeter.menu");
        }
        else
        {
            message = Prompts.Get("greeter.first-time");
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
                return (Prompts.Format("greeter.opener.mood.concern-topic", ("emotion", how), ("topic", topic)), topic);
            }

            var vibe = emotion is null ? "a bit low" : emotion;
            return (Prompts.Format("greeter.opener.mood.concern", ("vibe", vibe)), null);
        }

        if (mood.Trend == MoodTrend.Improving && mood.AverageValence < 0.2)
            return (Prompts.Get("greeter.opener.mood.improving"), null);

        if (mood.RecentMood is Sentiment.Positive or Sentiment.VeryPositive)
        {
            if (topic is not null)
                return (Prompts.Format("greeter.opener.mood.positive-topic", ("topic", topic), ("emotion", emotion ?? "upbeat")), topic);
            return (Prompts.Get("greeter.opener.mood.positive"), null);
        }

        return (null, null);
    }

    private static string LowerFirst(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
