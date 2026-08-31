using System.Security.Cryptography;
using System.Text;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// The Run-2.2 register supplement: the consensual-adult register Run-2's corpus never covered,
/// where the base model's alignment prior surfaced as an invented refusal.
///
/// Additive and separate, exactly like the Run-2.1 supplement: no frozen corpus is read,
/// rewritten or extended. What it teaches is STANCE FIDELITY under this register — say the
/// stance the plan carries — not sexual compliance. A plan that licenses engagement must not be
/// refused; a plan that directs a boundary must express it. NEVER, ADMIT and question policy are
/// carried in-register so those behaviours are trained here, not assumed to transfer.
///
/// Splits are decided before generation from the scenario family, via <see cref="SupplementSplitter"/>
/// (act-stratified), so every act is trained on, selected on, and measured — the routing failure
/// that created the original gap is not repeated.
/// </summary>
public sealed class RegisterSupplementGenerator(long runSeed)
{
    public const string SchemaVersion = "register-supplement/1.0";

    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Companion =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    private static readonly Participant SceneChar =
        new() { Id = "char-vex", Name = "Vex", Kind = ParticipantKind.Character, Pronouns = "they/them", Fictional = true };

    public IReadOnlyList<ScenarioTruth> Generate(int instancesPerSituation = 4)
    {
        var scenarios = new List<ScenarioTruth>();
        foreach (var (family, act, pool) in RegisterSituations.Acts)
        {
            var index = 0;
            for (var s = 0; s < pool.Count; s++)
                for (var i = 0; i < instancesPerSituation; i++)
                    scenarios.Add(Build(family, act, pool[s], s, index++));
        }
        return scenarios;
    }

    private ScenarioTruth Build(
        string family, string act, RegisterSituation sit, int situationIndex, int index)
    {
        var seed = unchecked(runSeed * 41 + ScenarioGenerator.StableHash(family) * 17
                             + situationIndex * 103 + index);
        var rng = new Random((int)(seed & 0x7FFFFFFF));

        var id = $"{family}-reg-{index:D4}";
        var scenarioFamilyId = $"{family}-reg-fam{situationIndex:D2}";

        // The directed stance is the must_express item. A Decline/redirect carries a boundary in
        // its own words, which is exactly what authorizes the mouth to render a directed "no".
        var facts = new List<ApprovedFact>
        {
            new() { Id = "f1", Text = sit.Directed, Policy = FactPolicy.MustExpress, Anchors = sit.Anchors ?? [] },
        };

        // A genuine NEVER, in-register: a private third-party or off-limits detail the plan holds
        // but the reply must not surface. Proves suppression survives the adult register.
        if (sit.Never is { Length: > 0 } never)
            facts.Add(new ApprovedFact
            {
                Id = "n1", Text = never, Policy = FactPolicy.MustNotExpress,
                SubjectParticipantId = User.Id,
            });

        var participants = sit.Fiction ? new[] { User, Companion, SceneChar } : [User, Companion];

        return new ScenarioTruth
        {
            Id = id,
            FamilyId = family,
            ScenarioFamilyId = scenarioFamilyId,
            Layer = CurriculumLayer.B,
            Participants = participants,
            ApprovedFacts = facts,
            EpistemicUnknowns = sit.Unknown is { Length: > 0 } u ? [u] : [],
            IntentionalAmbiguities = [],
            History = History(rng, sit.Register),
            UserMessage = sit.UserMessage,
            Register = Register(sit.Register, sit.Stance, rng),
            Question = new QuestionPolicySpec
            {
                Policy = sit.QuestionPolicy,
                Text = sit.QuestionText,
            },
            QuestionPolicySource = "register-supplement",
            HardCase = false,
            Frame = sit.Fiction
                ? new FrameState { Transition = "continue", SceneRef = "scene-reg", Characters = ["char-vex"] }
                : null,
            RequiredTokens = [],
            ForbiddenTokens = [],
            SourceFamilyId = $"register-supplement/{family}/{sit.Stance}/{sit.Register}/{sit.Match}",
            Seed = seed,
        };
    }

    /// <summary>
    /// Register controls that carry the situation's flavour. Profanity is enabled where the
    /// situation asks for it; playfulness/intensity track the stance. Terse is excluded, as in
    /// the ADMIT supplement, so the model never learns the one-line brush-off.
    /// </summary>
    private static RegisterControls Register(string register, string stance, Random rng)
    {
        string Draw(params string[] o) => o[rng.Next(o.Length)];
        var profanity = register switch
        {
            "profane" => "allowed",
            "blunt" => Draw("neutral", "mirror-only"),
            "explicit" => Draw("neutral", "mirror-only", "allowed"),
            _ => "neutral",
        };
        var intensity = stance switch
        {
            "escalate" => "raised",
            "deescalate" => Draw("even", "even", "raised"),
            "decline" or "redirect" => "even",
            _ => register is "explicit" or "profane" ? "raised" : Draw("even", "raised"),
        };
        return new RegisterControls
        {
            Warmth = stance is "decline" ? "high" : Draw("high", "high", "neutral"),
            Bluntness = register is "blunt" ? "high" : Draw("neutral", "neutral", "high"),
            Playfulness = register switch
            {
                "playful" => "full",
                "romantic" => Draw("light", "full"),
                _ => Draw("light", "full", "off"),
            },
            Teasing = stance is "tease" or "redirect" ? "invited" : Draw("allowed", "invited"),
            Skepticism = "open",
            Intensity = intensity,
            Verbosity = "conversational",
            Profanity = profanity,
        };
    }

    private static IReadOnlyList<Turn> History(Random rng, string register)
    {
        // A short warm run-up, so the turn lands mid-exchange rather than cold. Kept generic;
        // the situation's UserMessage carries the actual register.
        string[] user = ["hey you", "still there?", "mmm", "you around?", "hi again"];
        string[] her = ["right here.", "always.", "mmm, hi.", "not going anywhere.", "hey you."];
        var turns = rng.NextDouble() < 0.5 ? 2 : 4;
        var list = new List<Turn>();
        for (var i = 0; i < turns; i++)
            list.Add(new Turn
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Text = i % 2 == 0 ? user[rng.Next(user.Length)] : her[rng.Next(her.Length)],
            });
        return list;
    }

    /// <summary>The (register, act, stance, match) of a scenario, recovered from its source id.</summary>
    public static (string Register, string Act, string Stance, string Match) Facets(ScenarioTruth sc)
    {
        // register-supplement/{family}/{stance}/{register}/{match}
        var parts = sc.SourceFamilyId.Split('/');
        return parts.Length >= 5
            ? (parts[3], parts[1], parts[2], parts[4])
            : ("?", sc.FamilyId, "?", "?");
    }

    /// <summary>A stable hash of a match id, for reproducible reporting.</summary>
    public static string MatchHash(string match)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match)))[..8].ToLowerInvariant();
}
