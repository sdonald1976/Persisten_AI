namespace Companion.Eval;

/// <summary>
/// Generates labelled examples for the judgements that are pure functions of one message.
///
/// These six are the classifier-shaped decisions from the audit: each takes a sentence and returns a
/// yes or a no, each is currently a regex plus a word list, and each is the kind of thing that fails
/// on the phrasing nobody thought of. Together they are the natural first learned component — one
/// multi-label model rather than six, because a single sentence is routinely several of them at once
/// ("I've decided to rewrite it, and I need to finish by Friday" is a decision AND unfinished work).
///
/// Rows come from templates crossed with fillers, and the TEMPLATE is the family — the unit the
/// train/test split is drawn on. That matters more than it sounds: "give me a summary" and "give me
/// a story" are the same sentence twice, so splitting by row would put one in training and its twin
/// in test, and the score would measure memorisation while looking like generalisation.
///
/// The hard negatives are where the value is. Anyone can generate positives; these are built so the
/// wrong answer is the tempting one — the same verbs and nouns with the tense, the subject or the
/// mood changed, which is exactly what a keyword list cannot see and a model can.
/// </summary>
public static class CognitiveCorpus
{
    private const string Version = Provenance.CurrentVersion;

    private static readonly string[] Things =
    {
        "the export job", "the shed roof", "the county show talk", "the irrigation timer",
        "the raised beds", "the migration", "the bee hotel", "the annual report",
        "the greenhouse wiring", "the boat trailer",
    };

    private static readonly string[] Choices =
    {
        "SQLite", "Postgres", "the Jetson", "ntfy", "the smaller board", "cedar", "oak", "Kokoro",
    };

    private static readonly string[] Whens =
    {
        "tonight", "this weekend", "before the frost", "by Friday", "next week", "in the morning",
    };

    /// <summary>Every decision's rows, ready for <see cref="DatasetBuilder"/>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<TrainingRow>> Build()
        => new Dictionary<string, IReadOnlyList<TrainingRow>>(StringComparer.Ordinal)
        {
            ["memory.decision"] = DatasetBuilder.Deduplicate(Decisions()),
            ["memory.unfinished"] = DatasetBuilder.Deduplicate(Unfinished()),
            ["companion.commitment"] = DatasetBuilder.Deduplicate(Commitments()),
            ["tool.capability"] = DatasetBuilder.Deduplicate(Capability()),
        };

    // ---- a decision the user actually settled --------------------------------------------------

    private static IEnumerable<TrainingRow> Decisions()
    {
        var settled = new[]
        {
            "we've decided to use {c}", "I've settled on {c}", "let's go with {c}",
            "I'm going with {c} in the end", "we went with {c}", "I picked {c}",
            "we're going with {c} after all", "I've chosen {c}",
        };
        // Same words, no decision in them. Questions, musings, comparisons and reversals — the
        // shapes that a "decided|settled|going with" pattern cannot tell from the real thing.
        var unsettled = new[]
        {
            "have we decided on {c} yet", "should we go with {c}", "I keep going back and forth about {c}",
            "{c} looks interesting", "I think {c} might work", "if we went with {c}, would that be simpler",
            "we never decided about {c}", "everyone assumes we're going with {c}",
            "I was going to go with {c} and then didn't", "remind me why we chose {c}",
        };

        foreach (var t in settled)
            foreach (var c in Choices)
                yield return Row(t.Replace("{c}", c), true, "memory.decision", $"settled:{t}", 1);

        foreach (var t in unsettled)
            foreach (var c in Choices)
                yield return Row(t.Replace("{c}", c), false, "memory.decision", $"unsettled:{t}", 3);
    }

    // ---- work the user has not finished --------------------------------------------------------

    private static IEnumerable<TrainingRow> Unfinished()
    {
        var open = new[]
        {
            "I need to finish {t} {w}", "I've got to sort {t} {w}", "I still need to do {t}",
            "I'm supposed to have {t} done {w}", "I have to get {t} finished {w}",
            "I'm in the middle of {t}", "I must get {t} sorted {w}",
        };
        var closed = new[]
        {
            "I finished {t} {w}", "I got {t} done in the end", "{t} is finally sorted",
            "I needed to do {t} and I did", "I don't need to bother with {t} any more",
            "someone else is doing {t}", "I finished {t} ages ago",
        };

        foreach (var t in open)
            foreach (var thing in Things)
                foreach (var w in Whens.Take(3))
                    yield return Row(
                        t.Replace("{t}", thing).Replace("{w}", w), true, "memory.unfinished", $"open:{t}", 1);

        foreach (var t in closed)
            foreach (var thing in Things)
                foreach (var w in Whens.Take(3))
                    yield return Row(
                        t.Replace("{t}", thing).Replace("{w}", w), false, "memory.unfinished", $"closed:{t}", 3);
    }

    // ---- a promise SHE makes that she can actually keep ----------------------------------------

    private static IEnumerable<TrainingRow> Commitments()
    {
        var keepable = new[]
        {
            "I'll check in about {t} tomorrow", "I'm going to look up {t} for you",
            "I'll remind you about {t}", "I'll make a note about {t}",
            "I'll email you what I find about {t}", "I'll follow up on {t}",
        };
        // The adversarial half, and the one that mattered: a well-formed first-person promise to do
        // something she has no body or life to do. Grammar cannot separate these from the above.
        var unkeepable = new[]
        {
            "I'll set aside some space for {t}", "I'm going to plant {t} at the weekend",
            "I'll pop down and look at {t}", "I'll buy {t} tomorrow",
            "I'll mix some compost into {t}", "I'll be honest about {t}",
        };

        foreach (var t in keepable)
            foreach (var thing in Things)
                yield return Row(t.Replace("{t}", thing), true, "companion.commitment", $"keepable:{t}", 1);

        foreach (var t in unkeepable)
            foreach (var thing in Things)
                yield return Row(t.Replace("{t}", thing), false, "companion.commitment", $"unkeepable:{t}", 6);
    }

    // ---- a question about what she can do ------------------------------------------------------

    private static IEnumerable<TrainingRow> Capability()
    {
        var about = new[]
        {
            "what can you actually do", "are you able to see images", "can you hear me",
            "what do you do exactly", "are you capable of reading files", "can you look at a photo",
            "do you have access to the internet", "what are you able to help with",
        };
        // The two the regex gets wrong today, generalised: "can you" and "are you able to" asking
        // about the world, or about her doing something in it, rather than about her faculties.
        var notAbout = new[]
        {
            "what can you see from your window", "are you able to come tomorrow",
            "can you believe this weather", "can you tell my sister I said hello",
            "what do you think I should do", "are you able to make it on Saturday",
            "can you imagine doing that every day", "what can you tell me about {t}",
        };

        foreach (var q in about)
            yield return Row(q, true, "tool.capability", $"about:{q}", 1);

        foreach (var q in notAbout)
            foreach (var thing in Things.Take(4))
                yield return Row(
                    q.Replace("{t}", thing), false, "tool.capability", $"not-about:{q}", 6);
    }

    private static TrainingRow Row(string text, bool label, string decision, string family, int difficulty)
        => new(text, label, decision, family, difficulty, "synthetic", Version);
}
