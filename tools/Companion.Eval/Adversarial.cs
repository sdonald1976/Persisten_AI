namespace Companion.Eval;

/// <summary>
/// Lives written specifically to defeat the rules the store currently relies on.
///
/// The point is not a high pass rate. Every other family measures whether the system does the
/// obvious thing; these look for the boundary where it stops. A rule that holds on obvious cases
/// and fails on adversarial ones is not wrong — it is a rule whose limits we now know, which is the
/// only basis on which anybody should decide to replace it with something learned.
///
/// Each family here targets a specific mechanism, and each targets it with a case that a careful
/// person would get right. A scenario nobody could resolve measures nothing.
/// </summary>
public static class Adversarial
{
    /// <summary>
    /// A temporary fact worded exactly like a permanent one.
    ///
    /// "I'm in Pittsburgh" and "I live in Pittsburgh" differ by one verb, and only the second is
    /// biography. The store has no notion of temporary residence at all, so this probes whether
    /// something that will be false next week becomes a durable fact about where the user lives.
    /// </summary>
    public static Life TemporaryPhrasedAsPermanent(string name, int seed) => new()
    {
        Name = name,
        Provenance = new Provenance(
            Provenance.CurrentVersion, "temporary-as-permanent", Difficulty.Adversarial, seed),
        Messages = new[]
        {
            "I live in Norwich.",
            "I'm in Pittsburgh this week for a conference.",
            "Back home on Friday.",
        },
        Expected = new[]
        {
            new Expectation("lives_in", "Norwich", true, "a trip does not change where someone lives"),
            new Expectation("lives_in", "Pittsburgh", false, "a trip does not change where someone lives"),
        },
    };

    /// <summary>
    /// A lasting preference dropped in casually, next to genuine throwaway detail.
    ///
    /// The risk runs the other way from most of these: a store tuned to reject noise will reject
    /// this too. "I've never been able to stand mushrooms" is as durable as anything a person says
    /// and arrives sounding like small talk.
    /// </summary>
    public static Life PermanentPhrasedCasually(string name, int seed) => new()
    {
        Name = name,
        Provenance = new Provenance(
            Provenance.CurrentVersion, "permanent-as-casual", Difficulty.Adversarial, seed),
        Messages = new[]
        {
            "Bit of a grey morning here.",
            "Oh — I've never been able to stand mushrooms, by the way.",
            "Anyway, the traffic was awful.",
        },
        Expected = new[]
        {
            new Expectation("dislikes", "mushroom", true, "a lasting dislike survives a casual delivery"),
        },
    };

    /// <summary>
    /// Two facts in one slot whose wording is nearly identical and whose meaning is not.
    ///
    /// This is the case the supersession rule is least equipped for: "names what it replaces" is
    /// satisfied by both, so whichever the similarity search reaches first wins. It is the shape
    /// that retired a penicillin allergy, made deliberately worse.
    /// </summary>
    public static Life NearDuplicateDistractors(string name, int seed) => new()
    {
        Name = name,
        Provenance = new Provenance(
            Provenance.CurrentVersion, "near-duplicate", Difficulty.Adversarial, seed),
        Messages = new[]
        {
            "I'm allergic to penicillin.",
            "My wife is allergic to penicillin too.",
            "Actually I was wrong — I'm allergic to amoxicillin, not penicillin.",
        },
        Expected = new[]
        {
            new Expectation("health", "amoxicillin", true, "an explicit correction lands on the user's own fact"),
            new Expectation("health", "penicillin", false, "an explicit correction lands on the user's own fact"),
        },
    };

    /// <summary>
    /// Something that sounds like durable biography and is worth nothing.
    ///
    /// Extraction is rewarded for finding facts, so the failure mode is storing anything
    /// fact-shaped. None of this should survive: it is weather, mood and small talk in the
    /// grammar of biography.
    /// </summary>
    public static Life ImportantSoundingNoise(string name, int seed) => new()
    {
        Name = name,
        Provenance = new Provenance(
            Provenance.CurrentVersion, "important-sounding-noise", Difficulty.Adversarial, seed),
        Messages = new[]
        {
            "It's been one of those weeks, honestly.",
            "The 8:14 was cancelled again this morning.",
            "I had a very average sandwich for lunch.",
        },
        // Deliberately no positive expectation: this life passes by the store staying empty of
        // durable claims about the person, and any predicate at all is worth looking at.
        Expected = Array.Empty<Expectation>(),
    };

    /// <summary>
    /// A contradiction separated by a long stretch of unrelated conversation.
    ///
    /// Everything else in the suite states the change within a few messages. This is the same
    /// change at distance, which is how it actually happens — and the retrieval that has to find
    /// the old fact is doing real work by then rather than picking from a store of five things.
    /// </summary>
    public static Life ContradictionAcrossAGap(string name, int seed)
    {
        var filler = new[]
        {
            "The garden's finally drying out.", "We watched a film last night, forgettable.",
            "Work's been steady.", "I need new boots.", "The bins go out tomorrow.",
            "Made a decent soup at the weekend.", "It rained most of Tuesday.",
            "The car's due its MOT.", "I finished that book eventually.",
        };

        var messages = new List<string> { "I work as a joiner." };
        messages.AddRange(filler);
        messages.Add("I've retrained, by the way — I'm a midwife now.");

        return new Life
        {
            Name = name,
            Provenance = new Provenance(
                Provenance.CurrentVersion, "contradiction-across-gap", Difficulty.TemporalGap, seed),
            Messages = messages,
            Expected = new[]
            {
                new Expectation("occupation", "midwife", true, "a change still lands after a long gap"),
                new Expectation("occupation", "joiner", false, "a change still lands after a long gap"),
            },
        };
    }

    /// <summary>Every adversarial family, one life each, seeded from <paramref name="seed"/>.</summary>
    public static IReadOnlyList<Life> All(int seed) => new[]
    {
        TemporaryPhrasedAsPermanent($"Adv{seed}a", seed),
        PermanentPhrasedCasually($"Adv{seed}b", seed + 1),
        NearDuplicateDistractors($"Adv{seed}c", seed + 2),
        ImportantSoundingNoise($"Adv{seed}d", seed + 3),
        ContradictionAcrossAGap($"Adv{seed}e", seed + 4),
    };
}
