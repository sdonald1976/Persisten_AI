using Companion.Core.Domain;
using Companion.Core.Text;

namespace Companion.Core.Services;

/// <summary>
/// A place the world says exists, as the companion hears about it.
///
/// Deliberately transient. This is never stored, never cached, and never persisted — the world
/// advertises its layout on every connection, and the companion picks from what it was just told.
/// The design rule is that she may hold a connection but never a model; a table of places in
/// <c>companion.db</c> would be that model, arriving one reasonable commit at a time.
/// </summary>
public sealed record WorldPlace(string Id, string Name, string Description);

/// <summary>Where she has decided to go, and why. The reason is the point.</summary>
public sealed record RoamingChoice(string PlaceId, string Reason, double Score);

/// <summary>
/// Decides where she goes, from her own state and the menu the world just sent.
///
/// Pure, deterministic, and free of any model call. That is a cost decision — a language model
/// consulted every time she might move would compete with the conversation for one GPU — but more
/// importantly it makes her movement <em>answerable</em>. Every choice carries the reason that
/// produced it, so "why are you in the greenhouse?" has a true answer rather than a plausible one,
/// which is the same standard the rest of the companion is held to.
///
/// It reads the world's own words for each place rather than holding opinions about named rooms.
/// A world with entirely different places works unchanged.
/// </summary>
public static class RoamingPolicy
{
    /// <summary>
    /// How much better somewhere else must be before she actually gets up. Without a margin she
    /// paces — every tiny change of mood would move her, which reads as agitation rather than life.
    /// </summary>
    /// <summary>
    /// How much better somewhere else must be before she actually gets up. This is the *only*
    /// hysteresis: an earlier version also gave the current room a bonus, which double-counted the
    /// same idea and made the two cancel out so precisely that she never moved at all.
    /// </summary>
    public const double MoveThreshold = 0.25;

    /// <summary>Weight for "this place is about the thing I'm preoccupied with".</summary>
    private const double PreoccupationWeight = 1.0;

    /// <summary>Weight for "this place suits how much energy I have".</summary>
    private const double EnergyWeight = 0.8;

    /// <summary>Weight for "this place suits my spirits".</summary>
    private const double SpiritsWeight = 0.5;

    /// <summary>Penalty for the room she just left, so she doesn't oscillate between two doors.</summary>
    private const double JustLeftPenalty = 0.5;

    /// <summary>Matching words needed before a place counts as fully suiting a mood.</summary>
    private const double AffinityMatchesForFull = 2.0;

    // Read against the world's own description of a place. Not a list of rooms — a list of what
    // words suggest a place is *for*.
    private static readonly string[] Restful =
        { "quiet", "calm", "rest", "warm", "cosy", "cozy", "soft", "still", "bed", "sit" };

    private static readonly string[] Active =
        { "work", "desk", "make", "build", "tend", "grow", "cook", "task", "tool" };

    private static readonly string[] Open =
        { "outside", "open", "sky", "air", "garden", "light", "sun", "wide" };

    /// <summary>
    /// Picks somewhere to be, or null to stay put.
    /// </summary>
    /// <param name="places">What the world just advertised. Empty means no world to move in.</param>
    /// <param name="currentPlace">Where she is now, if the world said.</param>
    /// <param name="previousPlace">Where she was before that, to avoid pacing between two rooms.</param>
    /// <param name="state">Her spirits and energy.</param>
    /// <param name="preoccupations">
    /// What is on her mind — open curiosities, attention items. Free text; only the content words
    /// matter, matched against what the world says each place is.
    /// </param>
    public static RoamingChoice? Choose(
        IReadOnlyList<WorldPlace> places,
        string? currentPlace,
        string? previousPlace,
        CompanionStateSnapshot state,
        IReadOnlyList<string> preoccupations)
    {
        if (places.Count == 0)
            return null;

        var scored = places
            .Select(place => Score(place, currentPlace, previousPlace, state, preoccupations))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.PlaceId, StringComparer.Ordinal) // ties resolve the same way every time
            .ToList();

        var best = scored[0];

        if (currentPlace is not null && string.Equals(best.PlaceId, currentPlace, StringComparison.OrdinalIgnoreCase))
            return null; // already in the best place

        var here = scored.FirstOrDefault(c =>
            currentPlace is not null && string.Equals(c.PlaceId, currentPlace, StringComparison.OrdinalIgnoreCase));

        if (here is not null && best.Score - here.Score < MoveThreshold)
            return null; // not enough in it to be worth getting up

        return best;
    }

    private static RoamingChoice Score(
        WorldPlace place,
        string? currentPlace,
        string? previousPlace,
        CompanionStateSnapshot state,
        IReadOnlyList<string> preoccupations)
    {
        var about = $"{place.Name} {place.Description}";
        var score = 0.0;
        string? reason = null;

        // 1. What is she thinking about? The strongest pull, and the most legible: going to the
        //    greenhouse because she is wondering about the greenhouse is a reason a person would give.
        var (topical, matched) = Preoccupation(place.Name, about, preoccupations);
        if (topical > 0)
        {
            score += topical * PreoccupationWeight;
            reason = $"she's been wondering about {matched}";
        }

        // 2. Does this place suit her energy? Low energy seeks somewhere restful, high energy
        //    somewhere with something to do.
        var restful = Affinity(about, Restful);
        var active = Affinity(about, Active);
        var energyFit = state.Energy >= 0.65 ? active - restful
            : state.Energy <= 0.4 ? restful - active
            : 0.0;

        if (energyFit > 0)
        {
            score += energyFit * EnergyWeight;
            reason ??= state.Energy >= 0.65
                ? "she has the energy for something"
                : "it's a low hour and this is somewhere quiet";
        }

        // 3. Do her spirits suit it? Low spirits draw inward, bright ones outward.
        var openness = Affinity(about, Open);
        var spiritsFit = state.Spirits >= 0.3 ? openness
            : state.Spirits <= -0.3 ? restful - openness
            : 0.0;

        if (spiritsFit > 0)
        {
            score += spiritsFit * SpiritsWeight;
            reason ??= state.Spirits >= 0.3
                ? "she's in bright spirits and this is somewhere open"
                : "things have felt heavy and this is somewhere enclosed";
        }

        if (previousPlace is not null && string.Equals(place.Id, previousPlace, StringComparison.OrdinalIgnoreCase))
            score -= JustLeftPenalty;

        return new RoamingChoice(place.Id, reason ?? "no particular reason", Math.Round(score, 4));
    }

    /// <summary>
    /// How strongly a place matches anything on her mind, and which thing matched most. Uses the
    /// same tokenizer as memory keyword scoring, so "content word" means the same thing here as
    /// everywhere else in the companion.
    /// </summary>
    private static (double Score, string? Matched) Preoccupation(
        string name, string about, IReadOnlyList<string> preoccupations)
    {
        var here = new HashSet<string>(Tokenizer.Tokenize(about));
        var named = new HashSet<string>(Tokenizer.Tokenize(name));
        if (here.Count == 0)
            return (0, null);

        var best = 0.0;
        string? matched = null;

        foreach (var thought in preoccupations)
        {
            var words = new HashSet<string>(Tokenizer.Tokenize(thought));
            if (words.Count == 0)
                continue;

            // How much of what she is thinking about is present here — not Jaccard, which
            // punishes a short thought against a long description and would make every
            // preoccupation look weak.
            //
            // Being *named* counts for more than being mentioned. Rooms describe their
            // neighbours ("outside, past the greenhouse"), so a thought about the greenhouse
            // otherwise matches the garden exactly as well as the greenhouse, and she walks
            // confidently to the wrong room.
            var inBody = (double)words.Count(here.Contains) / words.Count;
            var inName = (double)words.Count(named.Contains) / words.Count;
            var coverage = (inBody * 0.6) + (inName * 0.4);

            if (coverage > best)
            {
                best = coverage;
                matched = Summarise(thought);
            }
        }

        return (best, matched);
    }

    /// <summary>
    /// How strongly a place reads as suiting a mood, from the world's own words for it. Counted
    /// rather than measured as a fraction of the description: a longer, more evocative description
    /// should not score *lower* for being descriptive, which is what dividing by length did.
    /// </summary>
    private static double Affinity(string text, string[] vocabulary)
    {
        var tokens = Tokenizer.Tokenize(text);
        var hits = tokens.Count(t => vocabulary.Any(v => v.StartsWith(t, StringComparison.Ordinal)
                                                         || t.StartsWith(v, StringComparison.Ordinal)));
        return Math.Min(1.0, hits / AffinityMatchesForFull);
    }

    /// <summary>Trims a preoccupation to something that reads inside a sentence.</summary>
    private static string Summarise(string thought)
    {
        var trimmed = thought.Trim().TrimEnd('?', '.', '!');
        return trimmed.Length <= 60 ? trimmed : trimmed[..60].TrimEnd() + "…";
    }
}
