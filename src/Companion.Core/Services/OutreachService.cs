using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// The companion reaching out on her own — with every restraint that keeps unprompted contact
/// welcome. She only messages when the user has genuinely been away, the budget window has
/// passed, it's a decent hour, AND she holds a real open curiosity to voice. No curiosity, no
/// message: she never sends an empty "hey" just because a timer fired.
///
/// Sending is voicing: the curiosity is spent by the notification exactly as it would be by a
/// greeting opener, so she won't ask it again in-app — instead the next session naturally
/// continues from it. The outreach log records every send (provenance + budget).
/// </summary>
public sealed class OutreachService : IOutreachService
{
    /// <summary>A passed event stays worth a "how did it go?" for this long; older is let go.</summary>
    internal static readonly TimeSpan FollowUpWindow = TimeSpan.FromDays(7);

    private readonly IOutboundChannel _channel;
    private readonly IConversationStore _conversations;
    private readonly IOutreachStore _outreach;
    private readonly IReflectionStore _reflections;
    private readonly IAnticipationStore _anticipations;
    private readonly IProfileStore _profiles;
    private readonly IPersonalityService _personality;
    private readonly OutreachOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutreachService> _logger;

    public OutreachService(
        IOutboundChannel channel,
        IConversationStore conversations,
        IOutreachStore outreach,
        IReflectionStore reflections,
        IAnticipationStore anticipations,
        IProfileStore profiles,
        IPersonalityService personality,
        IOptions<OutreachOptions> options,
        TimeProvider clock,
        ILogger<OutreachService> logger)
    {
        _channel = channel;
        _conversations = conversations;
        _outreach = outreach;
        _reflections = reflections;
        _anticipations = anticipations;
        _profiles = profiles;
        _personality = personality;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<OutboundMessage?> RunOnceAsync(string userId, CancellationToken ct = default)
    {
        if (!_channel.Configured)
            return null;

        var now = _clock.GetUtcNow();

        // Gate 1: she must have met the user — never a cold message to someone who's never talked.
        var lastSeen = await _conversations.GetLastMessageAtAsync(userId, ct);
        if (lastSeen is null)
            return null;

        // Gate 2: the budget. Rare is what keeps it special (and not creepy).
        var lastSent = await _outreach.GetLastSentAtAsync(userId, ct);
        if (lastSent is not null && now - lastSent < TimeSpan.FromHours(_options.MinHoursBetween))
            return null;

        // Gate 3: a decent hour, in the server's local time.
        if (IsQuietHour(_clock.GetLocalNow().Hour, _options.QuietStartHour, _options.QuietEndHour))
            return null;

        var today = _clock.GetLocalNow().Date;
        var anticipations = await _anticipations.GetOpenAsync(userId, ct);

        // Something to say, in priority order. Event-day encouragement deliberately skips the
        // away-gate: "good luck today" has to land on the morning even if you chatted last night —
        // arriving before the event IS the feature.
        var eventToday = anticipations.FirstOrDefault(
            a => a.Status == AnticipationStatus.Pending && a.EventAt.Date == today);
        if (eventToday is not null)
        {
            return await DeliverAsync(
                userId, Prompts.Format("outreach.goodluck", ("description", eventToday.Description)),
                $"anticipation:{eventToday.Id}", now,
                onSent: () => _anticipations.MarkEncouragedAsync(userId, eventToday.Id, now, ct), ct);
        }

        // Everything below is "you've been gone a while" contact — the away-gate applies.
        if (now - lastSeen < TimeSpan.FromHours(_options.AwayHours))
            return null;

        // A passed event that never got its follow-up (recent enough to still be current).
        var passed = anticipations.FirstOrDefault(
            a => a.IsOpen && a.EventAt.Date < today && today - a.EventAt.Date <= FollowUpWindow);
        if (passed is not null)
        {
            return await DeliverAsync(
                userId, Prompts.Format("outreach.followup", ("description", passed.Description)),
                $"anticipation:{passed.Id}", now,
                onSent: () => _anticipations.MarkFollowedUpAsync(userId, passed.Id, now, ct), ct);
        }

        // Otherwise: the freshest curiosity from her between-session reflection. Its question is
        // already phrased to the user, which is exactly the tone an unprompted message should have.
        var curiosity = (await _reflections.GetOpenCuriositiesAsync(userId, ct)).FirstOrDefault();
        if (curiosity is null)
            return null;

        return await DeliverAsync(
            userId, Prompts.Format("outreach.curiosity", ("question", curiosity.Question)),
            $"curiosity:{curiosity.Id}", now,
            onSent: () => _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct), ct);
    }

    /// <summary>
    /// Delivery first; state changes only after the message actually reached the outside world. A
    /// failed send burns nothing — the source stays open and the budget untouched, so a later
    /// check simply retries.
    /// </summary>
    private async Task<OutboundMessage?> DeliverAsync(
        string userId, string text, string source, DateTimeOffset now, Func<Task> onSent, CancellationToken ct)
    {
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var name = _personality.Identity(profile).Name;
        var title = string.IsNullOrWhiteSpace(name) ? "Your companion" : name.Trim();

        if (!await _channel.SendAsync(title, text, ct))
        {
            _logger.LogWarning("Outreach delivery failed for {UserId}; will retry on a later check.", userId);
            return null;
        }

        var message = new OutboundMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Text = text,
            Source = source,
            SentAt = now,
        };
        await _outreach.AddAsync(message, ct);
        await onSent();

        _logger.LogInformation("Reached out to {UserId}: \"{Text}\"", userId, text);
        return message;
    }

    /// <summary>Quiet-hours check that handles a window crossing midnight; equal start/end = disabled.</summary>
    public static bool IsQuietHour(int hour, int start, int end)
        => start != end && (start < end ? hour >= start && hour < end : hour >= start || hour < end);
}
