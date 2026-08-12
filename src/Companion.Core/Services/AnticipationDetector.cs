using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Spots a dated upcoming event in the user's own words — "I have an interview tomorrow",
/// "my friend's graduation is on Saturday" — and resolves the day, so the companion can wish
/// them luck on the morning and ask how it went after. Deliberately conservative, like the
/// commitment and mood detectors: it only fires on a clear event-noun + day-word pairing and
/// misses some rather than inventing plans the user never stated. Day precision only — that's
/// how people talk about plans.
/// </summary>
public static partial class AnticipationDetector
{
    private const string WhenTokens =
        "tomorrow|today|tonight|next week|monday|tuesday|wednesday|thursday|friday|saturday|sunday";

    // "I have an interview tomorrow" / "I've got a dentist appointment on Friday"
    [GeneratedRegex(
        @"\bi\s+(?:have|'?ve\s+got|have\s+got|got)\s+(?<art>a|an|my|the)\s+(?<what>[a-z][a-z' -]{2,40}?)\s+(?:coming\s+up\s+)?(?:on\s+|this\s+)?(?<when>" + WhenTokens + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HaveEvent();

    // "my interview is on Thursday" / "the recital is tomorrow"
    [GeneratedRegex(
        @"\b(?<art>my|the)\s+(?<what>[a-z][a-z' -]{2,40}?)\s+is\s+(?:on\s+|this\s+)?(?<when>" + WhenTokens + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventIs();

    // Nouns that pattern-match but aren't events — better to miss than to "look forward" to a hunch.
    private static readonly string[] NotEvents =
    {
        "feeling", "hunch", "suspicion", "thought", "idea", "question", "lot", "bit", "day off",
    };

    /// <summary>What was detected: a user-facing phrase, the (local-midnight) event day, and the evidence.</summary>
    public sealed record DetectedAnticipation(string Description, DateTimeOffset EventAt, string Evidence);

    /// <summary>
    /// Returns the first dated event in <paramref name="message"/>, or null. <paramref name="localNow"/>
    /// anchors relative day words ("tomorrow", "Thursday") — pass the clock's local now.
    /// </summary>
    public static DetectedAnticipation? Detect(string? message, DateTimeOffset localNow)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        foreach (var regex in new[] { HaveEvent(), EventIs() })
        {
            foreach (Match m in regex.Matches(message))
            {
                var what = m.Groups["what"].Value.Trim().TrimEnd('.', ',', ';');
                if (what.Length < 3 || NotEvents.Any(n => what.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var day = ResolveDay(m.Groups["when"].Value, localNow);
                if (day is null)
                    continue;

                // "my X" → "your X"; everything else reads naturally as "the X".
                var article = m.Groups["art"].Value.ToLowerInvariant();
                var description = (article == "my" ? "your " : "the ") + what.ToLowerInvariant();

                var evidence = m.Value.Trim();
                return new DetectedAnticipation(
                    description,
                    new DateTimeOffset(day.Value, localNow.Offset), // local midnight of the event day
                    evidence.Length <= 200 ? evidence : evidence[..200]);
            }
        }

        return null;
    }

    /// <summary>Resolves a day word to a concrete date (relative to the local now). Null if unknown.</summary>
    private static DateTime? ResolveDay(string when, DateTimeOffset localNow)
    {
        var today = localNow.Date;
        switch (when.Trim().ToLowerInvariant())
        {
            case "today" or "tonight":
                return today;
            case "tomorrow":
                return today.AddDays(1);
            case "next week":
                return today.AddDays(7);
        }

        if (!Enum.TryParse<DayOfWeek>(when, ignoreCase: true, out var target))
            return null;

        // The NEXT occurrence of that weekday — "on Thursday" said on a Thursday means next week
        // ("today" covers the same-day case).
        var days = ((int)target - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }
}
