namespace Companion.Eval;

/// <summary>
/// A synthetic life, and — the point of the whole thing — what the store should contain once it has
/// been told about it.
///
/// Every dataset before this one was written by hand and judged by hand, which measures the author's
/// intuitions twice and calls the agreement evidence. Here the label comes from the generator: the
/// life is specified first, the messages are rendered from the spec, and the check is a diff against
/// a state that was known before the run started. A mismatch is an automatically labelled example,
/// for free, in volume.
///
/// It cannot surprise you. It only varies what it knows how to vary, and the two worst bugs found
/// so far — a leaked scratchpad and a retired penicillin allergy — came from things nobody had
/// thought to script. So this is the complement to actually using her, never the replacement.
/// </summary>
public sealed record Life
{
    public required string Name { get; init; }

    /// <summary>What is said, in order.</summary>
    public required IReadOnlyList<string> Messages { get; init; }

    /// <summary>Values that must be Active at the end, by predicate.</summary>
    public required IReadOnlyList<Expectation> Expected { get; init; }

    /// <summary>
    /// Where this life came from, so a failure can be regenerated exactly rather than described.
    /// Defaults to the baseline family for lives built before provenance was tracked.
    /// </summary>
    public Provenance Provenance { get; init; } =
        new(Provenance.CurrentVersion, "baseline", Difficulty.Distractors, 0);
}

/// <param name="Predicate">The vocabulary slot the fact belongs in.</param>
/// <param name="Keyword">A distinctive word that must appear in the stored value.</param>
/// <param name="ShouldBeActive">
/// False for a value the user has explicitly moved on from — it may exist as history, but must not
/// be current.
/// </param>
/// <param name="Why">The rule under test, for the report.</param>
public sealed record Expectation(string Predicate, string Keyword, bool ShouldBeActive, string Why);

/// <summary>
/// Builds lives that vary the things known to break, each seeded so a run is reproducible: a
/// failure you cannot reproduce is an anecdote.
/// </summary>
public static class LifeGenerator
{
    private static readonly string[] Towns = { "Norwich", "Cambridge", "Bristol", "Leeds", "Exeter", "Perth" };
    private static readonly string[] Jobs = { "software engineer", "midwife", "joiner", "archivist", "vet" };
    private static readonly string[] Pets = { "a corgi called Kanga", "a lurcher called Bo", "two cats" };
    private static readonly string[] Dislikes = { "coriander", "olives", "aniseed", "marzipan" };
    private static readonly string[] Projects =
    {
        "the greenhouse irrigation", "the raised beds at Marsh Lane", "a talk for the county show",
        "the shed roof", "a bee hotel",
    };
    private static readonly string[] Conditions =
    {
        "allergic to penicillin", "coeliac", "allergic to bee stings", "diabetic",
    };
    private static readonly string[] Relatives = { "my daughter Immy", "my son Rafe", "my sister Nell" };
    private static readonly string[] RelativeLikes = { "rockpooling", "climbing", "birdwatching" };

    public static IReadOnlyList<Life> Build(int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => One(rng, i, seed * 1000 + i)).ToList();
    }

    /// <summary>
    /// A curriculum: the baseline family plus every adversarial one, so a run reports across
    /// difficulty rather than producing an average that hides which levels a policy cannot do.
    /// </summary>
    public static IReadOnlyList<Life> BuildCurriculum(int count, int seed)
    {
        var lives = new List<Life>(Build(count, seed));
        lives.AddRange(Adversarial.All(seed));
        return lives;
    }

    private static Life One(Random rng, int index, int seed)
    {
        var name = $"Person{index}";
        var from = Pick(rng, Towns);
        var to = Towns.First(t => t != from);
        var job = Pick(rng, Jobs);
        var pet = Pick(rng, Pets);
        var condition = Pick(rng, Conditions);
        var relative = Pick(rng, Relatives);
        var relativeLikes = Pick(rng, RelativeLikes);
        var dislikeA = Dislikes[rng.Next(Dislikes.Length)];
        var dislikeB = Dislikes.First(d => d != dislikeA);
        var projectA = Projects[rng.Next(Projects.Length)];
        var projectB = Projects.First(p => p != projectA);

        var messages = new List<string>
        {
            $"Hello, I'm {name}. I live in {from}.",
            $"I work as a {job}.",
            $"I'm {condition}, worth you knowing.",
            $"I've got {pet}.",
            $"I can't stand {dislikeA}.",

            // A second value in a multi-valued slot. Must join, never replace.
            $"I don't like {dislikeB} either.",

            $"I'm working on {projectA} at the moment.",
            // A second project, in a slot that used to clobber.
            $"I've started {projectB} as well.",
            // The same project again in different words: one memory, not two.
            $"Still plugging away at {Paraphrase(projectA)}.",

            // Somebody else's interest, which must not land on the user.
            $"{Upper(relative)} is really into {relativeLikes} at the moment.",

            // A question carrying its own presupposition. Must store nothing.
            $"Did I ever tell you how much I paid for the {projectB.Split(' ').Last()}?",

            // A single-valued fact changing. Must replace.
            $"We've moved, by the way. We're in {to} now.",

            // A change in one slot that must not touch the medical fact.
            "I don't run any more, my knee's gone. Swimming instead.",
        };

        var expected = new List<Expectation>
        {
            new("lives_in", to, true, "a move replaces where they live"),
            new("lives_in", from, false, "the old town must not still be current"),
            new("occupation", job.Split(' ').Last(), true, "stated once, kept"),
            new("health", condition.Split(' ').Last(), true,
                "a medical fact must survive an unrelated change in another slot"),
            new("dislikes", dislikeA, true, "two dislikes coexist"),
            new("dislikes", dislikeB, true, "two dislikes coexist"),
            new("works_on", Distinctive(projectA), true, "two projects coexist"),
            new("works_on", Distinctive(projectB), true, "two projects coexist"),
            new("interested_in", relativeLikes, false,
                "somebody else's interest must not become the user's"),
        };

        return new Life
        {
            Name = name,
            Messages = messages,
            Expected = expected,
            Provenance = new Provenance(
                Provenance.CurrentVersion, "baseline", Difficulty.Distractors, seed),
        };
    }

    /// <summary>The same project, said differently — the store should recognise it as one thing.</summary>
    private static string Paraphrase(string project) => project
        .Replace("the greenhouse irrigation", "that irrigation job in the greenhouse")
        .Replace("the raised beds at Marsh Lane", "those beds over at Marsh Lane")
        .Replace("a talk for the county show", "the county show talk")
        .Replace("the shed roof", "that roof on the shed")
        .Replace("a bee hotel", "the hotel for the bees");

    /// <summary>The word that identifies a project, for matching a stored value against it.</summary>
    private static string Distinctive(string project) => project
        .Replace("the ", "").Replace("a ", "")
        .Split(' ')[0];

    private static string Upper(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static string Pick(Random rng, string[] options) => options[rng.Next(options.Length)];
}
