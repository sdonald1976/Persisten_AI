using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The familiarity dial: derives the relationship's stage from how long they've known each
/// other and how much they've actually talked, taking the LOWER of the two reads (time without
/// talk isn't closeness; talk without time isn't either). Purely derived — nothing stored,
/// nothing to drift — and it only ever moves forward as real history accumulates.
/// </summary>
public sealed class FamiliarityTracker : IFamiliarityTracker
{
    private readonly IConversationStore _conversations;
    private readonly TimeProvider _clock;

    public FamiliarityTracker(IConversationStore conversations, TimeProvider clock)
    {
        _conversations = conversations;
        _clock = clock;
    }

    public async Task<FamiliaritySnapshot> BuildAsync(string userId, CancellationToken ct = default)
    {
        var first = await _conversations.GetFirstMessageAtAsync(userId, ct);
        var messages = await _conversations.CountUserMessagesAsync(userId, ct);
        var days = first is { } start ? Math.Max(0, (_clock.GetUtcNow() - start).TotalDays) : 0;

        return new FamiliaritySnapshot
        {
            DaysKnown = days,
            UserMessages = messages,
            Stage = ComputeStage(days, messages),
        };
    }

    /// <summary>The lower of the tenure read and the depth read — both have to be earned.</summary>
    public static FamiliarityStage ComputeStage(double daysKnown, int userMessages)
    {
        var byDays = daysKnown switch
        {
            < 3 => FamiliarityStage.New,
            < 21 => FamiliarityStage.Acquainted,
            < 90 => FamiliarityStage.Familiar,
            _ => FamiliarityStage.Close,
        };
        var byMessages = userMessages switch
        {
            < 20 => FamiliarityStage.New,
            < 150 => FamiliarityStage.Acquainted,
            < 400 => FamiliarityStage.Familiar,
            _ => FamiliarityStage.Close,
        };
        return (FamiliarityStage)Math.Min((int)byDays, (int)byMessages);
    }
}
