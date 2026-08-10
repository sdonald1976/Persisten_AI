using Companion.Core.Abstractions;
using Companion.Core.Domain;

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
    private readonly TimeProvider _clock;

    public Greeter(
        IProjectStore projects,
        IMemoryStore memories,
        IConversationStore conversations,
        IRelationshipTracker relationship,
        TimeProvider clock)
    {
        _projects = projects;
        _memories = memories;
        _conversations = conversations;
        _relationship = relationship;
        _clock = clock;
    }

    public async Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
    {
        var openLoops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var projects = (await _projects.GetProjectsAsync(userId, ct))
            .OrderByDescending(p => p.LastActivityAt)
            .ToList();
        var memories = await _memories.GetRetrievableMemoriesAsync(userId, ct);

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
        var moodOpener = MoodOpener(mood);
        if (moodOpener is not null)
            openers.Add(moodOpener);

        // Follow up on what the companion itself promised (its own initiative).
        foreach (var c in commitments.Take(1))
            openers.Add($"Last time I said I'd {LowerFirst(c.Description.TrimEnd('.'))} — want to pick that up?");

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

        string message;
        if (hasHistory)
        {
            var lead = timeContext is null ? "Hey — good to see you." : $"It's been {timeContext}. Good to see you back.";
            message = lead + " Here's where we left things; " +
                      "pick whatever you feel like, or ignore them all and just say what's on your mind.";
        }
        else
        {
            message = "Hey — we haven't talked before, so there's nothing to catch up on yet. " +
                      "Tell me anything — what you're working on, something on your mind — or ask \"what can you do?\"";
        }

        return new Greeting { Message = message, TimeContext = timeContext, Openers = openers };
    }

    /// <summary>
    /// A low-pressure opener that acknowledges the recent emotional tone, or null when nothing is
    /// notable. Care first for a low stretch; warmth for good spirits; encouragement when climbing
    /// back up. Deterministic and never presumes why.
    /// </summary>
    private static string? MoodOpener(RelationshipSnapshot mood)
    {
        if (!mood.HasHistory)
            return null;

        var emotion = string.IsNullOrWhiteSpace(mood.RecentEmotion) ? null : mood.RecentEmotion;

        if (mood.RecentMood is Sentiment.Negative or Sentiment.VeryNegative)
        {
            var how = emotion is null ? "a bit low" : emotion;
            return $"You seemed {how} last time — I'm here if you want to talk about it.";
        }

        if (mood.Trend == MoodTrend.Improving && mood.AverageValence < 0.2)
            return "Last stretch felt rough — hope things have been looking up.";

        if (mood.RecentMood is Sentiment.Positive or Sentiment.VeryPositive)
            return "You were in good spirits last time — hope that's still going.";

        return null;
    }

    private static string LowerFirst(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
