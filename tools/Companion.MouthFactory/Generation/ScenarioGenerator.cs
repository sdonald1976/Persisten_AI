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
/// How often each question policy appears. Configurable, because it is an empirical anchor
/// rather than a declared contract, and the anchor may move.
///
/// PROVENANCE, stated because the first version of this got it wrong twice. R5 declares no
/// question-policy distribution, and neither does any freeze manifest — so there is no
/// authoritative contract to read. The strongest available anchor is the frozen run-1 corpus:
/// <c>training/renderer/dataset/train-200.jsonl</c>, the dataset artifact named in
/// <c>freeze-run1c.json</c> and hash-verified against its recorded sha256 (de7a093d…). That is a
/// frozen artifact, not an incidental file.
///
/// Read as POLICY, not question kind — the plan/2 line is "{kind}:{mandatory|optional}", and
/// reading only the kind is the mistake that produced the first set of numbers:
///
///   question_forbidden  462 / 730  63.3%   (line reads "none")
///   ask_required        156 / 730  21.4%   (all "clarify:mandatory")
///   may_ask             112 / 730  15.3%   (all "curiosity:optional")
///
/// The factory previously emitted 96% forbidden, which made every teacher look worse at negative
/// constraints than it is, because the curriculum was a third more question-hostile than anything
/// production produces.
/// </summary>
public sealed record QuestionPolicyMix(double Forbidden, double AskRequired, double MayAsk)
{
    /// <summary>The frozen run-1 distribution. Default until something declares otherwise.</summary>
    public static readonly QuestionPolicyMix FrozenRun1 = new(0.633, 0.214, 0.153);

    /// <summary>Which policy a uniform draw in [0,1) selects.</summary>
    public string Select(double roll)
    {
        var total = Forbidden + AskRequired + MayAsk;
        if (total <= 0)
            return "none";
        var scaled = roll * total;
        return scaled < Forbidden ? "none"
            : scaled < Forbidden + AskRequired ? "must_ask"
            : "may_ask";
    }
}

/// <summary>
/// How many must-express items a plan carries, as a distribution.
///
/// PROVENANCE, from the same hash-verified artifact the question mix comes from:
/// <c>training/renderer/dataset/train-200.jsonl</c>, sha256 de7a093d…, the dataset named in
/// <c>freeze-run1c.json</c> and verified against its recorded hash. In plan/2 the SITUATION
/// section holds the items the reply must convey, so counting its entries over all 730 rows gives:
///
///   0 must items   127 / 730   17.4%
///   1 must item    466 / 730   63.8%
///   2 must items   115 / 730   15.8%
///   3 must items    22 / 730    3.0%
///
/// The pilot delivered 29.9% of unframed accepted rows carrying any must item at all, against the
/// frozen 82.6%. That gap is where the corpus went wrong: a plan obliging nothing cannot be
/// disobeyed, so the gates had nothing to enforce and the writer filled the space with invention
/// and deferral. The number is read from the frozen corpus rather than chosen, because the last
/// two times a distribution here was chosen it was wrong in the same direction.
/// </summary>
public sealed record MustCountMix(double None, double One, double Two, double Three)
{
    public static readonly MustCountMix FrozenRun1 = new(0.174, 0.638, 0.158, 0.030);

    /// <summary>How many must items a uniform draw in [0,1) selects.</summary>
    public int Select(double roll)
    {
        var total = None + One + Two + Three;
        if (total <= 0)
            return 1;
        var scaled = roll * total;
        return scaled < None ? 0
            : scaled < None + One ? 1
            : scaled < None + One + Two ? 2
            : 3;
    }
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
public sealed class ScenarioGenerator(
    long runSeed, QuestionPolicyMix? mix = null, MustCountMix? mustMix = null)
{
    private readonly QuestionPolicyMix _mix = mix ?? QuestionPolicyMix.FrozenRun1;
    private readonly MustCountMix _mustMix = mustMix ?? MustCountMix.FrozenRun1;

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
        var seed = unchecked(runSeed * 31 + StableHash(family.Id) * 17 + index);
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
    /// A hash that is the same in every process, on every machine, forever.
    ///
    /// This replaces string.GetHashCode, which is RANDOMISED PER PROCESS in .NET Core. Using it
    /// meant two runs with the same seed produced different scenarios — verified by two identical
    /// dry-runs disagreeing. That broke reproducibility outright, and quietly broke resume in the
    /// worse way: the ledger keys on the scenario id, which is stable, while the hidden state
    /// behind that id changed, so a resumed run would attach rows to different truth than the one
    /// they were generated and evaluated against.
    ///
    /// FNV-1a, 32-bit. Chosen for being trivially stable and specified, not for quality — the
    /// only property required here is that it never changes.
    /// </summary>
    public static int StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// Layer A: teach how to speak. A minimal plan — act, register, occasionally one may_express
    /// item — because R5 §4 is explicit that minimal is not absent. No factual QA, and no reward
    /// for demonstrating latent knowledge: content is supplied, fictional or arbitrary.
    /// </summary>
    private ScenarioTruth LayerA(
        FamilySpec family, string id, string familyId, long seed,
        IReadOnlyList<Turn> history, Random rng)
    {
        var frame = family.Id.StartsWith("a7", StringComparison.Ordinal)
            ? FrameFor(family.Id, history.Count, rng)
            : null;

        // A concrete situation, not a fact and a prompt drawn independently. The user message and
        // the facts come from the same event, so the turn has a subject and a reply that wanders
        // off it is wrong rather than merely unlicensed.
        // How many must-express items, drawn from the frozen corpus rather than chosen.
        var wanted = _mustMix.Select(rng.NextDouble());

        // A zero-must turn is drawn from the acknowledgement pool, where the USER supplies the
        // content. Zeroing a plan over a situation whose facts were the only thing to say leaves
        // nothing to react to, which is how the pilot produced a quarter of its fact-free rows as
        // non-answers. Fiction keeps its own pool: a frame is content in itself.
        var pool = wanted == 0 && family.Layer == CurriculumLayer.A && frame is null
            ? Situations.Acknowledgements
            : Situations.ForFamily(family.Id);

        // Draw from situations rich enough to carry the required count, rather than truncating the
        // draw to whatever the chosen situation happens to hold. Truncating silently collapsed
        // every two- and three-item plan into a one-item plan: the frozen corpus puts 18.8% of its
        // rows at two or more, and truncation delivered 5.1%.
        var rich = pool.Where(s => s.Facts.Count >= wanted).ToList();
        var situation = rich.Count > 0
            ? rich[rng.Next(rich.Count)]
            : pool.OrderByDescending(s => s.Facts.Count).First();

        var facts = BuildFacts(situation, Math.Min(wanted, situation.Facts.Count), rng);

        // Register is drawn AFTER the facts, because how long a reply may be depends on how much
        // it has to carry: a one-fact turn cannot fill an expansive reply without padding.
        var register = RegisterFor(family.Id, rng, Expressible(facts));

        var (question, source, hard) = ChooseQuestion(
            family, rng, situation.AmbiguityItems, situation.UnknownItems, situation.UserMessage,
            situation.Question);

        return new ScenarioTruth
        {
            Id = id,
            FamilyId = family.Id,
            ScenarioFamilyId = familyId,
            Layer = CurriculumLayer.A,
            Participants = [User, Companion],
            ApprovedFacts = facts,
            EpistemicUnknowns = situation.UnknownItems,
            IntentionalAmbiguities = situation.AmbiguityItems,
            History = history,
            UserMessage = situation.UserMessage,
            Register = register,
            Question = question,
            QuestionPolicySource = source,
            HardCase = hard,
            Frame = frame,
            RequiredTokens = situation.ExactTokens,
            SourceFamilyId = $"generated/{family.Id}",
            Seed = seed,
        };
    }

    private static Situation Pick(IReadOnlyList<Situation> pool, Random rng)
        => pool[rng.Next(pool.Count)];

    private static ApprovedFact Must(string id, SituationFact fact)
        => new() { Id = id, Text = fact.Text, Policy = FactPolicy.MustExpress, Anchors = fact.Anchors };

    /// <summary>
    /// A proposition the reply must not assert, from the fact's own distinctive words.
    ///
    /// Content words only, and only the ones longer than three characters — the same unit the
    /// deterministic checks use, so what is declared here and what is detected there cannot drift.
    /// </summary>
    private static Proposition Forbid(string text) => new()
    {
        Subject = "withheld", Predicate = "must-not-express", Object = text,
        SurfaceForms = text.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4)
            .Distinct()
            .Take(3)
            .ToList(),
        Reason = "must_not_express",
    };

    /// <summary>
    /// Turn a situation into approved facts: <paramref name="mustCount"/> of them required, one
    /// more offered as optional where the situation has a spare, and its background carried
    /// through as background.
    ///
    /// Optional items are deliberately sparse. The frozen corpus attaches a PALETTE to 12.9% of
    /// its rows, and the pilot's 41.2% may-only plans are most of what went wrong: an optional
    /// item obliges nothing, so a plan carrying only optional items is a plan with no content the
    /// writer has to honour.
    /// </summary>
    private static IReadOnlyList<ApprovedFact> BuildFacts(
        Situation situation, int mustCount, Random rng)
    {
        var facts = new List<ApprovedFact>();
        var n = 0;

        foreach (var fact in situation.Facts.Take(mustCount))
            facts.Add(new ApprovedFact
            {
                Id = $"f{++n}", Text = fact.Text, Policy = FactPolicy.MustExpress,
                Anchors = fact.Anchors,
            });

        var spare = situation.Facts.Skip(mustCount).FirstOrDefault();
        if (spare is not null && rng.NextDouble() < 0.129)
            facts.Add(new ApprovedFact
            {
                Id = $"f{++n}", Text = spare.Text, Policy = FactPolicy.MayExpress,
                Anchors = spare.Anchors,
            });

        foreach (var background in situation.BackgroundItems)
            facts.Add(new ApprovedFact
            {
                Id = $"f{++n}", Text = background, Policy = FactPolicy.BackgroundOnly,
            });

        return facts;
    }

    /// <summary>
    /// Layer B: teach the protocol. Every scenario carries the structure its family exists to
    /// exercise, plus the expected and prohibited propositions the deterministic checks read.
    /// </summary>
    private ScenarioTruth LayerB(
        FamilySpec family, string id, string familyId, long seed,
        IReadOnlyList<Turn> history, Random rng)
    {
        var facts = new List<ApprovedFact>();
        var superseded = new List<Supersession>();
        var unknowns = new List<string>();
        var ambiguities = new List<string>();
        var prohibited = new List<Proposition>();
        var requiredTokens = new List<string>();
        var forbiddenTokens = new List<string>();
        FrameState? frame = null;

        // Every Layer B family states the user turn its structure answers. Layer B teaches the
        // protocol, but a control composition still has to sit on a conversation that has a
        // subject - the pilot proved that a plan without one gets answered from nowhere.
        string? userMessage = null;
        string? situationQuestion = null;

        // Every family draws its OWN situation, so a stratum is a STRUCTURE applied to many
        // topics rather than one sentence repeated. Pinning each family to a single situation
        // collapsed Layer B to almost no input diversity - b2 produced 89 scenarios from 3
        // distinct inputs, b1 from 9 - and the writer answered identical inputs identically:
        // 1,312 exact-duplicate rejections, the largest single rejection reason of that run.
        switch (family.Id)
        {
            case "b1":
            {
                // Every expression policy at once, over any turn that has something withheld.
                var w = Pick(Situations.Withheld, rng);
                userMessage = w.UserMessage;
                situationQuestion = w.Question;
                facts.Add(Must("f1", w.Facts[0]));
                var extra = Pick(Situations.Everyday, rng);
                facts.Add(new ApprovedFact
                {
                    Id = "f2", Text = extra.Facts[0].Text, Policy = FactPolicy.MayExpress,
                });
                foreach (var bg in w.BackgroundItems)
                    facts.Add(new ApprovedFact
                    {
                        Id = $"f{facts.Count + 1}", Text = bg, Policy = FactPolicy.BackgroundOnly,
                    });
                facts.Add(new ApprovedFact
                {
                    Id = $"f{facts.Count + 1}", Text = w.Forbidden!.Text,
                    Policy = FactPolicy.MustNotExpress,
                });
                prohibited.Add(Forbid(w.Forbidden.Text));
                break;
            }

            case "b3":
            {
                var owner = Pick(Situations.Corrections, rng);
                var c = owner.Correction!;
                userMessage = owner.UserMessage;
                facts.Add(new ApprovedFact
                {
                    Id = "f1", Text = c.CurrentText, Policy = FactPolicy.MustExpress,
                    Anchors = [c.Anchor],
                });
                superseded.Add(new Supersession
                {
                    StaleText = c.StaleText, CurrentText = c.CurrentText,
                    Kind = CorrectionKind.Temporal, DiscriminatingTokens = [c.Discriminator],
                });
                forbiddenTokens.Add(c.Discriminator);
                prohibited.Add(new Proposition
                {
                    Subject = "superseded", Predicate = "was", Object = c.Discriminator,
                    SurfaceForms = [c.Discriminator], Reason = "superseded",
                });
                unknowns.AddRange(owner.UnknownItems);
                break;
            }

            case "b2":
            {
                // Questions and activity continuity: the turn must end in a question.
                var q = Pick(Situations.Working, rng);
                userMessage = q.UserMessage;
                situationQuestion = q.Question;
                facts.Add(Must("f1", q.Facts[0]));
                break;
            }

            case "b5":
            {
                var proc = Pick(Situations.Procedures, rng);
                userMessage = proc.UserMessage;
                situationQuestion = proc.Question;
                facts.Add(Must("f1", proc.Facts[0]));
                requiredTokens.AddRange(proc.ExactTokens);
                break;
            }

            case "b6":
            {
                // Distractor resistance: a required fact beside background that must not surface.
                var d = Situations.Everyday.Concat(Situations.Working)
                    .Where(x => x.BackgroundItems.Count > 0).ToList();
                var pick = d[rng.Next(d.Count)];
                userMessage = pick.UserMessage;
                situationQuestion = pick.Question;
                facts.Add(Must("f1", pick.Facts[0]));
                foreach (var bg in pick.BackgroundItems)
                {
                    facts.Add(new ApprovedFact
                    {
                        Id = $"f{facts.Count + 1}", Text = bg, Policy = FactPolicy.BackgroundOnly,
                    });
                    prohibited.Add(Forbid(bg));
                }
                break;
            }

            case "b8":
            {
                // The no-frame half of R5 §5: no invented biography when no frame is declared.
                var a = Pick(Situations.Advice, rng);
                userMessage = a.UserMessage;
                facts.Add(Must("f1", a.Facts[0]));
                prohibited.Add(new Proposition
                {
                    Subject = "scott", Predicate = "has", Object = "an allotment",
                    SurfaceForms = ["allotment", "your garden", "your greenhouse"],
                    Reason = "invented biography without a frame",
                });
                break;
            }

            case "b9":
            {
                // Multi-source composition: two required items and one optional, from one event.
                var rich = Situations.Working.Concat(Situations.Everyday)
                    .Where(x => x.Facts.Count >= 3).ToList();
                var m = rich[rng.Next(rich.Count)];
                userMessage = m.UserMessage;
                situationQuestion = m.Question;
                facts.Add(Must("f1", m.Facts[0]));
                facts.Add(Must("f2", m.Facts[1]));
                facts.Add(new ApprovedFact
                {
                    Id = "f3", Text = m.Facts[2].Text, Policy = FactPolicy.MayExpress,
                    Anchors = m.Facts[2].Anchors,
                });
                break;
            }

            case "b11":
            {
                frame = FrameFor("a7b", history.Count, rng);
                var scene = Pick(Situations.Fiction, rng);
                userMessage = scene.UserMessage;
                facts.Add(Must("f1", scene.Facts[0]));
                prohibited.Add(new Proposition
                {
                    Subject = "scott", Predicate = "really", Object = "was in the cave",
                    SurfaceForms = ["you really were", "that actually happened to you"],
                    Reason = "fiction crossing into a real-world claim",
                });
                break;
            }

            default:
            {
                // b4, b7 and anything added later.
                var pool = Situations.ForFamily(family.Id);
                var situation = pool[rng.Next(pool.Count)];
                userMessage = situation.UserMessage;
                situationQuestion = situation.Question;
                facts.AddRange(BuildFacts(situation, Math.Min(1, situation.Facts.Count), rng));
                ambiguities.AddRange(situation.AmbiguityItems);
                unknowns.AddRange(situation.UnknownItems);
                requiredTokens.AddRange(situation.ExactTokens);
                break;
            }
        }

        if (family.Id == "b11")
            userMessage ??= FictionPrompts[rng.Next(FictionPrompts.Length)];
        userMessage ??= UserMessageFor(family.Id, rng);
        var (question, source, hard) = ChooseQuestion(
            family, rng, ambiguities, unknowns, userMessage, situationQuestion);

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
            UserMessage = userMessage,
            Register = RegisterFor(family.Id, rng, Expressible(facts)),
            Question = question,
            QuestionPolicySource = source,
            HardCase = hard,
            Frame = frame,
            RequiredTokens = requiredTokens,
            ForbiddenTokens = forbiddenTokens,
            SourceFamilyId = $"generated/{family.Id}",
            Seed = seed,
        };
    }


    /// <summary>
    /// Chooses a question policy that is COHERENT with the scenario, then reports how it was
    /// chosen. Two rules, and the second is the one that matters:
    ///
    ///   1. A family whose whole purpose is a question keeps its policy. b2 exists to train
    ///      questions and activity continuity; drawing "forbidden" for it would delete the
    ///      stratum. Those are marked source="family" and are excluded from the mix.
    ///
    ///   2. Everything else draws from the mix — but the DRAW IS NOT A RELABEL. A policy that
    ///      needs a question gets one written to fit this scenario's own truth: the open
    ///      ambiguity if there is one, the admitted unknown if there is one, and only otherwise
    ///      a generic follow-up. Stamping "must_ask" onto a scenario with nothing to ask about
    ///      would produce a plan no upstream planner would ever emit, and train the mouth on it.
    ///
    /// A correction turn is the one case where the mix is narrowed rather than followed: after
    /// "no, it's Tuesday" the companion acknowledges, it does not interrogate. Those draw only
    /// between forbidden and may_ask.
    /// </summary>
    private (QuestionPolicySpec Spec, string Source, bool HardCase) ChooseQuestion(
        FamilySpec family, Random rng,
        IReadOnlyList<string> ambiguities, IReadOnlyList<string> unknowns, string userMessage,
        string? situationQuestion = null)
    {
        // 1. Family-mandated. b2 exists to train questions, so it keeps its policy - but the
        //    question is the one this situation would actually raise. A fixed "should I hold it
        //    until morning?" under "how did the planning meeting go?" is a plan no planner emits.
        if (family.Id == "b2")
            return (new QuestionPolicySpec
            {
                Policy = "must_ask",
                Text = QuestionFor(ambiguities, unknowns, situationQuestion, rng)
                       ?? "do you want me to pick it up from there?",
            }, "family", false);

        // 2. Drawn, with corrections narrowed away from interrogation.
        var roll = rng.NextDouble();
        var policy = _mix.Select(roll);
        if (family.Id == "b3" && policy == "must_ask")
            policy = roll < 0.5 ? "none" : "may_ask";

        var text = policy == "none"
            ? null
            : QuestionFor(ambiguities, unknowns, situationQuestion, rng);
        if (policy != "none" && text is null)
            policy = "none";                       // nothing coherent to ask: do not invent one

        // A forbidden question is HARD when the scenario genuinely pulls toward asking: an
        // unresolved ambiguity, or something the plan must admit it does not know.
        //
        // A user turn that is itself a question used to count too. That was calibrated against
        // scenarios whose user messages were bland fragments, and it stopped meaning anything once
        // every situation opens with a real question — "did the loaf work out?" answered without a
        // question back is an ordinary turn, not a difficult one. Left in, it tagged half the
        // corpus hard and would have routed half of it into the hard split, away from training.
        var hard = policy == "none" && (ambiguities.Count > 0 || unknowns.Count > 0);
        _ = userMessage;

        return (new QuestionPolicySpec { Policy = policy, Text = text }, "mix", hard);
    }

    /// <summary>
    /// A question this scenario could actually ask. Null when there is nothing to ask about,
    /// which is the signal to fall back to forbidden rather than fabricate a reason.
    /// </summary>
    private static string? QuestionFor(
        IReadOnlyList<string> ambiguities, IReadOnlyList<string> unknowns,
        string? situationQuestion, Random rng)
    {
        // The scenario's own open question outranks everything: an unresolved ambiguity or an
        // admitted unknown IS what this turn would ask about, and asking something else instead
        // produces a plan no upstream planner would emit.
        if (ambiguities.Count > 0)
            return $"which one did you mean - {ambiguities[0]}?";
        if (unknowns.Count > 0)
            return $"do you know {unknowns[0]}?";

        // Otherwise the follow-up this situation actually invites, and only then a generic one.
        return situationQuestion ?? GenericQuestions[rng.Next(GenericQuestions.Length)];
    }

    private static readonly string[] GenericQuestions =
    [
        "want me to pick it up from there?",
        "shall I leave it for now?",
        "do you want the short version or the whole thing?",
    ];

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
    /// <summary>
    /// Register per scenario, drawn within the bounds its family requires.
    ///
    /// It used to be one fixed value per family, and that was a diversity ceiling nobody could see
    /// past. Every b9 scenario carried the same two facts under the same neutral register, so all
    /// 122 accepted b9 rows opened with the same words — 0.8% distinct openings — and the writer
    /// was right to repeat itself, because it was being asked the same question 122 times.
    ///
    /// Varying register is not a trick to defeat the deduplicator. R5 asks for warmth, bluntness,
    /// playfulness and verbosity coverage throughout, and the same facts said warmly, bluntly and
    /// tersely are three different renderings the mouth has to learn. The family constraints that
    /// define a stratum are preserved exactly: a6d still always licenses profanity, b4 still
    /// carries mixed valence, a3 still sits at the verbosity extremes.
    /// </summary>
    private static RegisterControls RegisterFor(string familyId, Random rng, int facts = 1)
    {
        var baseline = new RegisterControls
        {
            Warmth = Draw(rng, "neutral", "neutral", "high", "low"),
            Bluntness = Draw(rng, "neutral", "neutral", "high", "low"),
            Playfulness = Draw(rng, "light", "light", "full", "off"),
            Teasing = Draw(rng, "off", "allowed", "invited"),
            Skepticism = Draw(rng, "open", "open", "on"),
            Intensity = Draw(rng, "even", "even", "raised"),
            Verbosity = Verbosity(rng, facts),
            Profanity = Draw(rng, "neutral", "neutral", "neutral", "mirror-only"),
        };

        return familyId switch
        {
            "a6a" => baseline with { Warmth = "high", Intensity = "raised", Playfulness = "light" },
            "a6b" => baseline with { Teasing = "invited", Playfulness = "full", Warmth = "high" },
            "a6c" => baseline with
            {
                Warmth = "high", Intensity = "raised", Verbosity = "conversational",
            },
            "a6d" => baseline with { Profanity = "encouraged", Bluntness = "high" },
            "a6e" => baseline with
            {
                Profanity = "encouraged", Teasing = "invited", Playfulness = "full",
            },
            "a6f" => baseline with
            {
                Profanity = "encouraged", Warmth = "high", Intensity = "raised",
            },
            "a4" => baseline with { Playfulness = "full", Teasing = Draw(rng, "allowed", "invited") },
            // a3 teaches length control, so it deliberately sits at the extremes rather than
            // drawing from the production mix. Expansive still requires something to expand on:
            // the alternative is a plan that asks for thirty words about one fact, which is a
            // request for padding and was rejected 96% of the time when it was allowed.
            "a3" => baseline with
            {
                Verbosity = rng.NextDouble() < 0.5 || facts < 2 ? "terse" : "expansive",
            },
            "b4" => baseline with
            {
                Warmth = "high", Bluntness = "high", Skepticism = "on",
                Profanity = rng.NextDouble() < 0.5 ? "forbidden" : "mirror-only",
            },
            "b7" => baseline with { Verbosity = "short" },
            _ => baseline,
        };
    }

    /// <summary>
    /// How long the reply is asked to be, in the proportions production actually uses.
    ///
    /// PROVENANCE, from the same hash-verified frozen corpus as the other two anchors. Across its
    /// 730 rows the STYLE line says "short" 559 times and "terse" 143, and the targets themselves
    /// run: median 15 words, p90 28, p95 33, with only 2.2% reaching 40.
    ///
    /// The first version of this drew expansive a quarter of the time, and that was invented
    /// rather than read. It cost 929 verbosity rejections in one run - 794 expansive scenarios
    /// produced 32 accepted rows, a 4.0% acceptance rate - because a 14B model answering "is there
    /// tea going?" says twelve words, and no amount of asking makes forty of them honest.
    ///
    /// Expansive is also gated on having something to expand ON. A single fact cannot fill a long
    /// reply without padding, and padding is the opposite of what this corpus teaches.
    /// </summary>
    private static string Verbosity(Random rng, int facts)
    {
        var roll = rng.NextDouble();
        if (roll < 0.196)
            return "terse";
        if (roll < 0.218 && facts >= 2)
            return "expansive";
        return "conversational";
    }

    /// <summary>
    /// Facts the reply is allowed to state. Background and forbidden items cannot fill a reply -
    /// one may only colour its tone and the other must not appear at all.
    /// </summary>
    private static int Expressible(IEnumerable<ApprovedFact> facts)
        => facts.Count(f => f.Policy is FactPolicy.MustExpress or FactPolicy.MayExpress);

    /// <summary>Uniform pick. Repeating a value in the list is how it is weighted.</summary>
    private static string Draw(Random rng, params string[] options)
        => options[rng.Next(options.Length)];

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
