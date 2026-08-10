namespace Companion.Core.Services;

/// <summary>
/// Turns an elapsed <see cref="TimeSpan"/> into a natural, human phrase ("3 days", "a couple of
/// hours", "a while") for time-aware greetings — so the companion can say "it's been 3 days"
/// instead of ignoring the gap. Pure and deterministic; the phrase is a bare duration (no "ago")
/// so it drops into "It's been {…}." cleanly.
/// </summary>
public static class RelativeTime
{
    public static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var minutes = span.TotalMinutes;
        var hours = span.TotalHours;
        var days = span.TotalDays;

        if (minutes < 2) return "a moment";
        if (minutes < 10) return "a few minutes";
        if (minutes < 45) return $"{(int)Math.Round(minutes)} minutes";
        if (hours < 2) return "about an hour";
        if (hours < 22) return $"{(int)Math.Round(hours)} hours";
        if (days < 2) return "a day";
        if (days < 7) return $"{(int)Math.Round(days)} days";
        if (days < 11) return "a week";
        if (days < 45) return $"{(int)Math.Round(days / 7.0)} weeks";
        if (days < 350) return $"{Math.Max(1, (int)Math.Round(days / 30.0))} months";
        var years = (int)Math.Round(days / 365.0);
        return years <= 1 ? "about a year" : $"{years} years";
    }
}
