namespace Companion.Core.Domain;

/// <summary>
/// How far along the relationship actually is, derived from two honest numbers: how long
/// they've known each other and how much they've really talked. Both must agree — three months
/// of silence isn't closeness, and neither is a hundred messages in one frantic day — so the
/// stage is the LOWER of the two reads. This is what lets month three sound different from day
/// one without ever faking a history that didn't happen.
/// </summary>
public sealed record FamiliaritySnapshot
{
    public required double DaysKnown { get; init; }
    public required int UserMessages { get; init; }
    public required FamiliarityStage Stage { get; init; }

    /// <summary>Calibration guidance for the prompt (never recited back).</summary>
    public string Describe()
    {
        var stageLine = Stage switch
        {
            FamiliarityStage.New =>
                "You've only just met — be warm but don't presume closeness; no shorthand, " +
                "no teasing, no acting like you share history you don't have yet.",
            FamiliarityStage.Acquainted =>
                "You've been talking for a little while now — relaxed and personable fits, and " +
                "light references to earlier conversations land well. Closeness is still growing.",
            FamiliarityStage.Familiar =>
                "You know each other well by now — casual shorthand, gentle teasing, and " +
                "references to shared history all fit naturally.",
            _ =>
                "You're genuinely close after all this time — comfortable directness, in-jokes, " +
                "real teasing, and no need to fill every silence or soften every edge.",
        };

        return DaysKnown < 1
            ? stageLine
            : $"You've known each other for {Services.RelativeTime.Describe(TimeSpan.FromDays(DaysKnown))}. {stageLine}";
    }
}

/// <summary>Relationship stages, in order — the dial only ever reads forward from real history.</summary>
public enum FamiliarityStage
{
    New,
    Acquainted,
    Familiar,
    Close,
}
