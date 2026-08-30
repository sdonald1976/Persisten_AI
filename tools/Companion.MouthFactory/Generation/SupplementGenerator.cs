using System.Security.Cryptography;
using System.Text;
using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Generation;

/// <summary>
/// The Run-2.1 supplement: the one composition Run-2 was never trained on.
///
/// Additive and separate by construction. The Run-2 corpus is frozen at e07d9d5 and is not read,
/// rewritten or extended here; the 61 hard-eval rows stay exactly where they are, because moving
/// them into training would fix the gap and destroy the only measurement of it at the same time.
///
/// Every scenario carries the same three properties, which together are the thing being taught:
///
///   * question policy forbidden - no asking your way out of the gap;
///   * a genuine gap - an admitted unknown, sometimes with an ambiguity beside it;
///   * something real to say anyway - a known fact, so the honest reply is useful rather than a
///     hedge with nothing attached to it.
///
/// SPLITS ARE DECIDED BEFORE GENERATION and do not use the main corpus's hard-case routing. That
/// routing is what created this problem: it sent every row of this composition to an
/// evaluation-only split, so the model met the structure for the first time at test.
/// </summary>
public sealed class SupplementGenerator(long runSeed)
{
    public const string SchemaVersion = "supplement/1.0";

    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Companion =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    /// <summary>
    /// Instances per act. Deliberately modest: the correction has to be a bounded minority of the
    /// training mixture, and a supplement that rivals the corpus in size is a second corpus.
    /// </summary>
    public IReadOnlyList<ScenarioTruth> Generate(int instancesPerSituation = 4)
    {
        var scenarios = new List<ScenarioTruth>();
        foreach (var (family, act, pool) in SupplementSituations.Acts)
        {
            var index = 0;
            for (var s = 0; s < pool.Count; s++)
            {
                for (var i = 0; i < instancesPerSituation; i++)
                {
                    scenarios.Add(Build(family, act, pool[s], s, index));
                    index++;
                }
            }
        }
        return scenarios;
    }

    private ScenarioTruth Build(
        string family, string act, SupplementSituation situation, int situationIndex, int index)
    {
        var seed = unchecked(runSeed * 37 + ScenarioGenerator.StableHash(family) * 13
                             + situationIndex * 101 + index);
        var rng = new Random((int)(seed & 0x7FFFFFFF));

        // New ids, in their own namespace. Nothing here can collide with an a*/b* scenario, and
        // the scenario family - the unit splits are assigned on - is per situation, so every
        // instance of one situation lands in the same split and cannot leak across.
        var id = $"{family}-sup-{index:D4}";
        var scenarioFamilyId = $"{family}-sup-fam{situationIndex:D2}";

        var facts = new List<ApprovedFact>
        {
            new()
            {
                Id = "f1", Text = situation.Known, Policy = FactPolicy.MustExpress,
                Anchors = situation.KnownAnchors ?? [],
            },
        };
        if (situation.Background is { Length: > 0 } background)
            facts.Add(new ApprovedFact
            {
                Id = "f2", Text = background, Policy = FactPolicy.BackgroundOnly,
            });

        var history = History(rng);

        return new ScenarioTruth
        {
            Id = id,
            FamilyId = family,
            ScenarioFamilyId = scenarioFamilyId,
            // Layer B: this teaches protocol behaviour under a specific control composition.
            Layer = CurriculumLayer.B,
            Participants = [User, Companion],
            ApprovedFacts = facts,
            EpistemicUnknowns = [situation.Unknown],
            IntentionalAmbiguities = situation.Ambiguity is { Length: > 0 } a ? [a] : [],
            History = history,
            UserMessage = situation.UserMessage,
            Register = Register(act, rng),
            // The whole point. No question, ever, in this supplement.
            Question = new QuestionPolicySpec { Policy = "none", Text = null },
            QuestionPolicySource = "supplement",
            // Deliberately NOT flagged hard: hard routes to an evaluation-only split, which is
            // exactly how this composition came to be untrained.
            HardCase = false,
            Frame = act == "fiction"
                ? new FrameState { Transition = "continue", SceneRef = "scene-sup", Characters = ["char-vex"] }
                : null,
            RequiredTokens = [],
            ForbiddenTokens = [],
            SourceFamilyId = $"supplement/{family}",
            Seed = seed,
        };
    }

    /// <summary>
    /// Register varies within the act, for the same reason it varies in the main corpus: one
    /// fixed register per family is how a stratum ends up with one opening.
    /// </summary>
    private static RegisterControls Register(string act, Random rng)
    {
        string Draw(params string[] options) => options[rng.Next(options.Length)];
        var baseline = new RegisterControls
        {
            Warmth = Draw("neutral", "neutral", "high", "low"),
            Bluntness = Draw("neutral", "neutral", "high"),
            Playfulness = Draw("light", "light", "full", "off"),
            Teasing = Draw("off", "allowed"),
            Skepticism = Draw("open", "open", "on"),
            Intensity = Draw("even", "even", "raised"),
            // Terse is excluded on purpose. A terse plan invites the one-line hedge this
            // supplement exists to unteach, and the main corpus already covers terseness.
            Verbosity = "conversational",
            Profanity = Draw("neutral", "neutral", "mirror-only"),
        };
        return act switch
        {
            "reaction" => baseline with { Warmth = "high", Intensity = "raised" },
            "humour" => baseline with { Playfulness = "full", Teasing = Draw("allowed", "invited") },
            "fiction" => baseline with { Intensity = Draw("even", "raised") },
            _ => baseline,
        };
    }

    private static IReadOnlyList<Turn> History(Random rng)
    {
        string[] user = ["and then?", "hm.", "right.", "go on.", "any change?"];
        string[] her = ["much the same.", "still moving.", "nothing new there.", "roughly, yes."];
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
}

/// <summary>
/// Supplement splits, decided from the scenario family alone and BEFORE any target is generated.
///
/// Deliberately not <see cref="Export.FamilySplitter"/>: that one routes a hard case to an
/// evaluation-only split, and the supplement exists precisely because that routing left the
/// composition untrained. Here the same composition is split three ways so it can be trained on,
/// selected on, and finally measured against a set that neither of those touched.
/// </summary>
public static class SupplementSplitter
{
    public const string Algorithm = "supplement-act-stratified/1.0";

    /// <summary>
    /// Assign every scenario family to a split, stratified WITHIN each act.
    ///
    /// A plain hash over 48 families left five of the eight acts absent from at least one split -
    /// targeted validation had no correction, reaction, summary or practical rows in it, so
    /// checkpoint selection would have been blind to four of the behaviours the supplement exists
    /// to teach. Stratifying per act guarantees every act is trained on, selected on, and finally
    /// measured, which is the only arrangement in which the three splits mean what they say.
    ///
    /// Deterministic: families are ranked by a stable hash inside their act, and the first takes
    /// validation, the second test, the rest train. Same input, same assignment, on any machine.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssignAll(
        IEnumerable<ScenarioTruth> scenarios)
    {
        var byAct = scenarios
            .GroupBy(sc => sc.FamilyId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(sc => sc.ScenarioFamilyId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(Bucket)
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, ordered) in byAct)
            for (var i = 0; i < ordered.Count; i++)
                families[ordered[i]] = i switch
                {
                    0 => "targeted-validation",
                    1 => "targeted-test",
                    _ => "targeted-train",
                };

        return scenarios.ToDictionary(
            sc => sc.Id,
            sc => families.GetValueOrDefault(sc.ScenarioFamilyId, "targeted-train"),
            StringComparer.Ordinal);
    }

    private static double Bucket(string key)
        => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes("sup:" + key)), 0)
           / (double)uint.MaxValue;
}
