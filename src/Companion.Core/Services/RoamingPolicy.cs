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
    ///
    /// This is the *only* hysteresis. An earlier version also gave the current room a bonus, which
    /// double-counted the same idea and made the two cancel out so precisely that she never moved.
    /// It relaxes as she settles, so the guard against pacing does not become a guard against ever
    /// leaving.
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

    /// <summary>
    /// How long she can be somewhere before staying becomes its own reason to leave.
    ///
    /// Without this she is a statue. Her energy has a handful of levels across a day, so once she
    /// is in the place that suits the current one she has no reason to move again for hours, and
    /// anyone looking in sees nothing happen. People do not sit in one room for a working day, and
    /// "she'd been in the study a while" is a reason a person would actually give.
    ///
    /// It is deliberately the weakest signal: anything real — something on her mind, somewhere that
    /// suits her better — moves her long before this does.
    /// </summary>
    public static readonly TimeSpan Restless = TimeSpan.FromMinutes(45);

    /// <summary>
    /// How much a long stay counts against staying. Small: it is meant to break a tie between
    /// rooms that are otherwise equal, not to override a real preference.
    /// </summary>
    private const double SettledDrift = 0.2;

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
    /// <param name="timeInPlace">
    /// How long she has been where she is. Staying somewhere becomes its own mild reason to move,
    /// which is the difference between a companion who is at rest and one who is inert.
    /// </param>
    /// <param name="restlessAfter">
    /// How long that takes. Configurable because it is a judgement about how a person behaves at
    /// home rather than anything the code can determine, and the right answer is the one that
    /// looks right to whoever lives with her.
    /// </param>
    public static RoamingChoice? Choose(
        IReadOnlyList<WorldPlace> places,
        string? currentPlace,
        string? previousPlace,
        CompanionStateSnapshot state,
        IReadOnlyList<string> preoccupations,
        TimeSpan? timeInPlace = null,
        TimeSpan? restlessAfter = null)
    {
        if (places.Count == 0)
            return null;

        var settled = Settledness(timeInPlace, restlessAfter ?? Restless);

        var scored = places
            .Select(place => Score(place, currentPlace, previousPlace, state, preoccupations, settled))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.PlaceId, StringComparer.Ordinal) // ties resolve the same way every time
            .ToList();

        var best = scored[0];

        if (currentPlace is not null && string.Equals(best.PlaceId, currentPlace, StringComparison.OrdinalIgnoreCase))
            return null; // already in the best place

        var here = scored.FirstOrDefault(c =>
            currentPlace is not null && string.Equals(c.PlaceId, currentPlace, StringComparison.OrdinalIgnoreCase));

        // Getting up is hard at first and easier the longer she has been sitting. The margin that
        // keeps her from pacing over a trivial difference should not also keep her in one room all
        // day.
        var threshold = MoveThreshold * (1 - settled);

        if (here is not null && best.Score - here.Score < threshold)
            return null; // not enough in it to be worth getting up

        return best;
    }

    /// <summary>
    /// How settled she is, from 0 (just arrived) to 1 (been here long enough that moving needs no
    /// justification beyond having been here).
    /// </summary>
    private static double Settledness(TimeSpan? timeInPlace, TimeSpan restlessAfter) =>
        timeInPlace is null || restlessAfter <= TimeSpan.Zero
            ? 0
            : Math.Clamp(timeInPlace.Value / restlessAfter, 0, 1);

    private static RoamingChoice Score(
        WorldPlace place,
        string? currentPlace,
        string? previousPlace,
        CompanionStateSnapshot state,
        IReadOnlyList<string> preoccupations,
        double settled)
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

        // 2. Does this place suit her energy? Continuous rather than banded: her energy has only a
        //    few levels across a day, and thresholds turned those into two moods and therefore two
        //    moves — active all afternoon, restful all night, and dead zones between where nothing
        //    pulled at all. A gradual lean means the best place drifts through the day as she does.
        var restful = Affinity(about, Restful);
        var active = Affinity(about, Active);
        var lean = (state.Energy - 0.5) * 2;                 // -1 (spent) … +1 (lively)
        var energyFit = (active - restful) * lean;

        if (energyFit > 0)
        {
            score += energyFit * EnergyWeight;
            reason ??= lean > 0
                ? "she has the energy for something"
                : "it's a low hour and this is somewhere quiet";
        }

        // 3. Do her spirits suit it? Low spirits draw inward, bright ones outward — again as a
        //    lean rather than a switch.
        var openness = Affinity(about, Open);
        var mood = Math.Clamp(state.Spirits, -1, 1);
        var spiritsFit = (openness - restful) * mood;

        if (spiritsFit > 0)
        {
            score += spiritsFit * SpiritsWeight;

            // Only claim a mood when there is really one to claim. Making the lean continuous meant
            // the faintest positive nudge — one friendly message — started reporting itself as
            // "bright spirits", which is the kind of small overstatement the whole design is
            // supposed to refuse. A slight pull gets a slight reason.
            reason ??= mood switch
            {
                >= 0.3 => "she's in bright spirits and this is somewhere open",
                <= -0.3 => "things have felt heavy and this is somewhere enclosed",
                > 0 => "somewhere open appealed",
                _ => "somewhere enclosed appealed",
            };
        }

        if (previousPlace is not null && string.Equals(place.Id, previousPlace, StringComparison.OrdinalIgnoreCase))
            score -= JustLeftPenalty;

        // Having been here a long time counts slightly against here — otherwise, when nothing
        // distinguishes anywhere, she stays put forever on a tie and the day never turns over.
        if (settled > 0
            && currentPlace is not null
            && string.Equals(place.Id, currentPlace, StringComparison.OrdinalIgnoreCase))
        {
            score -= settled * SettledDrift;
            reason ??= "she'd been here a while";
        }

        return new RoamingChoice(place.Id, reason ?? "a change of room", Math.Round(score, 4));
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
