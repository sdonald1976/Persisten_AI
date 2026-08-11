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
    private readonly IOutboundChannel _channel;
    private readonly IConversationStore _conversations;
    private readonly IOutreachStore _outreach;
    private readonly IReflectionStore _reflections;
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

        // Gate 1: the user must actually be away — and must have talked to her at least once,
        // ever. She never cold-messages someone she hasn't met.
        var lastSeen = await _conversations.GetLastMessageAtAsync(userId, ct);
        if (lastSeen is null || now - lastSeen < TimeSpan.FromHours(_options.AwayHours))
            return null;

        // Gate 2: the budget. Rare is what keeps it special (and not creepy).
        var lastSent = await _outreach.GetLastSentAtAsync(userId, ct);
        if (lastSent is not null && now - lastSent < TimeSpan.FromHours(_options.MinHoursBetween))
            return null;

        // Gate 3: a decent hour, in the server's local time.
        if (IsQuietHour(_clock.GetLocalNow().Hour, _options.QuietStartHour, _options.QuietEndHour))
            return null;

        // Gate 4: something real to say — the freshest curiosity from her between-session
        // reflection. Its question is already phrased to the user, which is exactly the tone an
        // unprompted message should have.
        var curiosity = (await _reflections.GetOpenCuriositiesAsync(userId, ct)).FirstOrDefault();
        if (curiosity is null)
            return null;

        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var name = _personality.Identity(profile).Name;
        var title = string.IsNullOrWhiteSpace(name) ? "Your companion" : name.Trim();
        var text = $"You crossed my mind. {curiosity.Question}";

        // Delivery first; state changes only after it actually reached the outside world. A failed
        // send burns nothing — the curiosity stays open and the budget untouched, so it simply
        // tries again on a later check.
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
            Source = $"curiosity:{curiosity.Id}",
            SentAt = now,
        };
        await _outreach.AddAsync(message, ct);
        await _reflections.MarkVoicedAsync(userId, curiosity.Id, now, ct);

        _logger.LogInformation("Reached out to {UserId}: \"{Text}\"", userId, text);
        return message;
    }

    /// <summary>Quiet-hours check that handles a window crossing midnight; equal start/end = disabled.</summary>
    public static bool IsQuietHour(int hour, int start, int end)
        => start != end && (start < end ? hour >= start && hour < end : hour >= start || hour < end);
}
