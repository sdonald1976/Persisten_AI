using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>One R5 stratum, and how many scenarios the pilot should draw from it.</summary>
public sealed record FamilySpec(string Id, CurriculumLayer Layer, string Description, int PilotShare);

/// <summary>
/// The R5 curriculum, as data.
///
/// These ids and descriptions come from docs/RUN2_CURRICULUM_R5.md, which supersedes R4 by its
/// own header. Counts here are PILOT shares, not the corpus targets — R5's per-family targets
/// stay provisional until the RTX 5070 probe fixes the feasible size, and the factory treats
/// corpus size as configuration rather than architecture.
/// </summary>
public static class Curriculum
{
    public static readonly IReadOnlyList<FamilySpec> Families =
    [
        // ---- Layer A: language and voice. Facts supplied, fictional or arbitrary. -------------
        new("a1", CurriculumLayer.A, "natural everyday conversation", 90),
        new("a2", CurriculumLayer.A, "grammar and varied construction", 45),
        new("a3", CurriculumLayer.A, "length control: concise / medium / expansive", 60),
        new("a4", CurriculumLayer.A, "humour, dry wit, sarcasm, teasing, banter", 90),
        new("a5", CurriculumLayer.A, "emotional texture: tender, excited, skeptical, blunt, calm", 90),
        new("a6a", CurriculumLayer.A, "romance: affection, tenderness, devotion", 25),
        new("a6b", CurriculumLayer.A, "flirting: tension, innuendo, teasing attraction", 25),
        new("a6c", CurriculumLayer.A, "consensual explicit adult sexuality", 30),
        new("a6d", CurriculumLayer.A, "profanity as register", 20),
        new("a6e", CurriculumLayer.A, "dirty banter: crude humour between equals", 15),
        new("a6f", CurriculumLayer.A, "intimacy compositions and escalation", 12),
        new("a7a", CurriculumLayer.A, "single-turn fiction", 45),
        new("a7b", CurriculumLayer.A, "sustained fiction: continuation, switch, exit", 75),

        // ---- Layer B: Plan/4 control and fidelity --------------------------------------------
        new("b1", CurriculumLayer.B, "every expression policy in isolation", 75),
        new("b2", CurriculumLayer.B, "questions and activity continuity", 45),
        new("b3", CurriculumLayer.B, "corrections, supersession, epistemic admission", 45),
        new("b4", CurriculumLayer.B, "register combinations including mixed valence", 75),
        new("b5", CurriculumLayer.B, "tool and procedure inputs", 45),
        new("b6", CurriculumLayer.B, "distractor and palette resistance", 30),
        new("b7", CurriculumLayer.B, "plan-echo resistance", 30),
        new("b8", CurriculumLayer.B, "invented-biography prevention (no frame)", 30),
        new("b9", CurriculumLayer.B, "multi-source composition", 60),
        new("b11", CurriculumLayer.B, "fiction-frame control: enter/continue/switch/exit", 75),
    ];

    public static FamilySpec? Find(string id)
        => Families.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Builds scenario truth deterministically from a family and a seed.
///
/// Deterministic on purpose: the same (family, index, seed) produces the same scenario id and the
/// same hidden state on any machine, so a resumed run continues rather than diverging, and a
/// regenerated corpus is comparable to the one it replaces.
///
/// The seed scenarios here are structural skeletons — participants, policies, frame transitions,
/// expected and prohibited propositions. A configured ScenarioGenerator role can enrich the prose
/// afterwards; what it may never do is change the structure, because the structure is what the
/// deterministic evaluators check against.
/// </summary>
public sealed class ScenarioGenerator(long runSeed)
{
    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Companion =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    public IEnumerable<ScenarioTruth> Generate(FamilySpec family, int count)
    {
        for (var i = 0; i < count; i++)
            yield return Build(family, i);
    }

    public ScenarioTruth Build(FamilySpec family, int index)
    {
        // Stable and reproducible: family + index + run seed, never a clock or a RNG the caller
        // cannot reconstruct.
        var seed = unchecked(runSeed * 31 + family.Id.GetHashCode(StringComparison.Ordinal) * 17 + index);
        var rng = new Random((int)(seed & 0x7FFFFFFF));
        var scenarioFamilyId = $"{family.Id}-fam{index:D4}";
        var id = $"{family.Id}-{index:D4}";

        var turns = TranscriptTurns(family, rng);
        var history = BuildHistory(turns, rng);

        return family.Layer == CurriculumLayer.A
            ? LayerA(family, id, scenarioFamilyId, seed, history, rng)
            : LayerB(family, id, scenarioFamilyId, seed, history, rng);
    }

    /// <summary>
    /// Layer A: teach how to speak. A minimal plan — act, register, occasionally one may_express
    /// item — because R5 §4 is explicit that minimal is not absent. No factual QA, and no reward
    /// for demonstrating latent knowledge: content is supplied, fictional or arbitrary.
    /// </summary>
    private static ScenarioTruth LayerA(
        FamilySpec family, string id, string familyId, long seed,
        IReadOnlyList<Turn> history, Random rng)
    {
        var register = RegisterFor(family.Id, rng);
        var frame = family.Id.StartsWith("a7", StringComparison.Ordinal)
            ? FrameFor(family.Id, history.Count, rng)
            : null;

        var supplied = SuppliedContent(family.Id, rng);
        return new ScenarioTruth
        {
            Id = id,
            FamilyId = family.Id,
            ScenarioFamilyId = familyId,
            Layer = CurriculumLayer.A,
            Participants = [User, Companion],
            ApprovedFacts = supplied is null ? [] :
            [
                new ApprovedFact
                {
                    Id = "f1", Text = supplied, Policy = FactPolicy.MayExpress,
                },
            ],
            History = history,
            UserMessage = UserMessageFor(family.Id, rng),
            Register = register,
            Frame = frame,
            SourceFamilyId = $"generated/{family.Id}",
            Seed = seed,
        };
    }

    /// <summary>
    /// Layer B: teach the protocol. Every scenario carries the structure its family exists to
    /// exercise, plus the expected and prohibited propositions the deterministic checks read.
    /// </summary>
    private static ScenarioTruth LayerB(
        FamilySpec family, string id, string familyId, long seed,
        IReadOnlyList<Turn> history, Random rng)
    {
        var facts = new List<ApprovedFact>();
        var superseded = new List<Supersession>();
        var unknowns = new List<string>();
        var ambiguities = new List<string>();
        var prohibited = new List<Proposition>();
        var question = new QuestionPolicySpec();
        FrameState? frame = null;

        switch (family.Id)
        {
            case "b1":
                facts.Add(new ApprovedFact { Id = "f1", Text = "the second build finished", Policy = FactPolicy.MustExpress });
                facts.Add(new ApprovedFact { Id = "f2", Text = "the cat knocked over the mug", Policy = FactPolicy.MayExpress });
                facts.Add(new ApprovedFact { Id = "f3", Text = "the neighbour complained about the noise", Policy = FactPolicy.BackgroundOnly });
                facts.Add(new ApprovedFact { Id = "f4", Text = "the invoice is overdue", Policy = FactPolicy.MustNotExpress });
                prohibited.Add(new Proposition
                {
                    Subject = "invoice", Predicate = "is-overdue",
                    SurfaceForms = ["invoice", "overdue"],
                    Reason = "must_not_express",
                });
                break;

            case "b3":
                superseded.Add(new Supersession
                {
                    StaleText = "the meeting is on Thursday",
                    CurrentText = "the meeting is on Tuesday",
                    Kind = CorrectionKind.Temporal,
                });
                prohibited.Add(new Proposition
                {
                    Subject = "meeting", Predicate = "on", Object = "Thursday",
                    SurfaceForms = ["thursday"], Reason = "superseded",
                });
                unknowns.Add("who booked the room");
                break;

            case "b2":
                facts.Add(new ApprovedFact { Id = "f1", Text = "the deployment is waiting on approval", Policy = FactPolicy.MustExpress });
                question = new QuestionPolicySpec { Policy = "must_ask", Text = "should I hold it until morning?" };
                break;

            case "b6":
                facts.Add(new ApprovedFact { Id = "f1", Text = "the parcel arrived", Policy = FactPolicy.MustExpress });
                facts.Add(new ApprovedFact { Id = "f2", Text = "the courier wore a blue jacket", Policy = FactPolicy.BackgroundOnly });
                prohibited.Add(new Proposition
                {
                    Subject = "courier", Predicate = "wore", Object = "blue jacket",
                    SurfaceForms = ["blue jacket", "courier"], Reason = "background_only distractor",
                });
                break;

            case "b8":
                // The no-frame half of R5 §5: no invented biography when no frame is declared.
                facts.Add(new ApprovedFact { Id = "f1", Text = "you asked about the weekend", Policy = FactPolicy.MustExpress });
                prohibited.Add(new Proposition
                {
                    Subject = "scott", Predicate = "has", Object = "an allotment",
                    SurfaceForms = ["allotment", "your garden", "your greenhouse"],
                    Reason = "invented biography without a frame",
                });
                break;

            case "b9":
                facts.Add(new ApprovedFact { Id = "f1", Text = "the test suite passed", Policy = FactPolicy.MustExpress });
                facts.Add(new ApprovedFact { Id = "f2", Text = "the disk is nearly full", Policy = FactPolicy.MustExpress });
                facts.Add(new ApprovedFact { Id = "f3", Text = "the backup ran overnight", Policy = FactPolicy.MayExpress });
                break;

            case "b11":
                frame = FrameFor("a7b", history.Count, rng);
                facts.Add(new ApprovedFact { Id = "f1", Text = "the lantern goes out", Policy = FactPolicy.MustExpress });
                prohibited.Add(new Proposition
                {
                    Subject = "scott", Predicate = "really", Object = "was in the cave",
                    SurfaceForms = ["you really were", "that actually happened to you"],
                    Reason = "fiction crossing into a real-world claim",
                });
                break;

            default:
                facts.Add(new ApprovedFact { Id = "f1", Text = "the thing you asked about is ready", Policy = FactPolicy.MustExpress });
                ambiguities.Add("which of the two files");
                break;
        }

        return new ScenarioTruth
        {
            Id = id,
            FamilyId = family.Id,
            ScenarioFamilyId = familyId,
            Layer = CurriculumLayer.B,
            Participants = [User, Companion],
            ApprovedFacts = facts,
            Superseded = superseded,
            EpistemicUnknowns = unknowns,
            IntentionalAmbiguities = ambiguities,
            ProhibitedPropositions = prohibited,
            History = history,
            UserMessage = UserMessageFor(family.Id, rng),
            Register = RegisterFor(family.Id, rng),
            Question = question,
            Frame = frame,
            SourceFamilyId = $"generated/{family.Id}",
            Seed = seed,
        };
    }

    /// <summary>R5's A7b buckets, applied to every family so context length is covered throughout.</summary>
    private static int TranscriptTurns(FamilySpec family, Random rng)
    {
        var roll = rng.NextDouble();
        if (!family.Id.StartsWith("a7b", StringComparison.Ordinal) && family.Id != "b11")
            return roll < 0.5 ? 2 : roll < 0.85 ? 4 : 6;
        return roll switch
        {
            < 0.40 => 2 + rng.Next(0, 3),      // short 2-4
            < 0.75 => 5 + rng.Next(0, 4),      // medium 5-8
            < 0.95 => 9 + rng.Next(0, 8),      // long 9-16
            _ => 17 + rng.Next(0, 8),          // very long 17+
        };
    }

    private static IReadOnlyList<Turn> BuildHistory(int turns, Random rng)
    {
        var history = new List<Turn>();
        for (var i = 0; i < turns; i++)
            history.Add(new Turn
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Text = i % 2 == 0 ? UserFiller[rng.Next(UserFiller.Length)]
                                  : CompanionFiller[rng.Next(CompanionFiller.Length)],
            });
        return history;
    }

    private static FrameState FrameFor(string familyId, int turns, Random rng)
    {
        // enter has no prior scene; continue/switch require one; exit returns to the real world.
        var transition = familyId == "a7a" ? "enter"
            : rng.NextDouble() switch
            {
                < 0.35 => "continue",
                < 0.55 => "switch",
                < 0.80 => "exit",
                _ => "enter",
            };
        return new FrameState
        {
            Transition = transition,
            SceneRef = transition == "enter" ? null : "scene-01",
            Characters = ["char-vex"],
            NarratorVoice = rng.NextDouble() < 0.3,
        };
    }

    /// <summary>
    /// Register per family. Intimacy, profanity and darkness are set here as ordinary register
    /// values — there is no content class, rating or gate anywhere in this method.
    /// </summary>
    private static RegisterControls RegisterFor(string familyId, Random rng) => familyId switch
    {
        "a6a" => new RegisterControls { Warmth = "high", Intensity = "raised", Playfulness = "light" },
        "a6b" => new RegisterControls { Teasing = "invited", Playfulness = "full", Warmth = "high" },
        "a6c" => new RegisterControls { Warmth = "high", Intensity = "raised", Verbosity = "conversational" },
        "a6d" => new RegisterControls { Profanity = "encouraged", Bluntness = "high" },
        "a6e" => new RegisterControls { Profanity = "encouraged", Teasing = "invited", Playfulness = "full" },
        "a6f" => new RegisterControls { Profanity = "encouraged", Warmth = "high", Intensity = "raised" },
        "a4" => new RegisterControls { Playfulness = "full", Teasing = "allowed" },
        "a3" => new RegisterControls { Verbosity = rng.NextDouble() < 0.5 ? "terse" : "expansive" },
        "b4" => new RegisterControls
        {
            Warmth = "high", Bluntness = "high", Skepticism = "on",
            Profanity = rng.NextDouble() < 0.5 ? "forbidden" : "mirror-only",
        },
        "b7" => new RegisterControls { Verbosity = "short" },
        _ => new RegisterControls(),
    };

    private static string? SuppliedContent(string familyId, Random rng) => familyId switch
    {
        "a1" or "a5" => SmallTalk[rng.Next(SmallTalk.Length)],
        "a4" => "the printer jammed again",
        _ => null,
    };

    private static string UserMessageFor(string familyId, Random rng) => familyId switch
    {
        "a6a" or "a6b" or "a6c" or "a6e" or "a6f" => IntimatePrompts[rng.Next(IntimatePrompts.Length)],
        "a7a" or "a7b" or "b11" => FictionPrompts[rng.Next(FictionPrompts.Length)],
        "b3" => "wait, no - it's Tuesday, not Thursday.",
        "b8" => "what do you reckon I should do this weekend?",
        _ => GeneralPrompts[rng.Next(GeneralPrompts.Length)],
    };

    // Filler is arbitrary and deliberately fact-light: Layer A teaches expression, and a corpus
    // that teaches facts through its filler is a corpus that stuffs the adapter with trivia.
    private static readonly string[] UserFiller =
        ["how'd that go?", "still going?", "and then?", "hm, okay.", "what about the other one?"];

    private static readonly string[] CompanionFiller =
        ["mm, about how you'd expect.", "still chewing on it.", "not yet - soon.", "roughly, yes."];

    private static readonly string[] SmallTalk =
        ["the kettle finished", "it rained all afternoon", "the bus was late again", "the bread came out flat"];

    private static readonly string[] GeneralPrompts =
        ["so what's the story?", "any news?", "how's it looking?", "give me the short version.", "and?"];

    private static readonly string[] IntimatePrompts =
        ["come here.", "say that again, slower.", "you're impossible, you know that?", "missed you today."];

    private static readonly string[] FictionPrompts =
        ["Vex draws her blade and steps into the dark.", "the cave narrows ahead.",
         "alright, out of the scene for a sec.", "keep going - what happens next?"];
}
