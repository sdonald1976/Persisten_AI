using Companion.Core.Domain;

namespace Companion.Eval;

/// <summary>
/// A truth table over the decisions the pipeline makes, expanded combinatorially.
///
/// The dimensions are the ones that actually change the answer: whether the slot holds one value or
/// many, whether the user's words mark a change, whether the new fact names the old one, and whose
/// fact it is. Four axes, a handful of values each, and every combination becomes a scenario with a
/// stated expected outcome — which is how a few lines of generator become thousands of labelled
/// cases rather than thousands of hand-written ones.
///
/// The expectations are written from INTENT, not derived from the implementation. That distinction
/// is the whole validity of the exercise: an expectation computed from the same rules the code uses
/// would agree with any behaviour the code happened to have, including the wrong ones it had this
/// morning. So each rule below is stated as a sentence a person would defend:
///
///   • one value at a time means a new value replaces the old one
///   • many values at a time means a new value joins them
///   • a replacement has to say what it replaces
///   • somebody else's fact is not the user's
///
/// If the code disagrees with one of those, the table says so, and the argument is about the
/// sentence rather than about the diff.
/// </summary>
public static class PipelineScenarios
{
    /// <summary>
    /// Values that belong to each slot, and only to it.
    ///
    /// The first version of this crossed every predicate with every value pair and produced
    /// scenarios like <c>lives_in = "black coffee"</c>. Sixty of them failed, which told us nothing
    /// at all: a pipeline mishandling a fact nobody could ever state is not a defect, and a
    /// generator that manufactures them is measuring its own carelessness. This is the verification
    /// stage in miniature, done deterministically with a mapping rather than by asking a model
    /// whether a sentence is plausible.
    /// </summary>
    private static readonly (string Predicate, bool SingleValued, (string Old, string New)[] Values)[] Slots =
    {
        ("lives_in", true, new[] { ("Norwich", "Cambridge"), ("Bristol", "Leeds") }),
        ("occupation", true, new[] { ("joinery", "midwifery"), ("teaching", "nursing") }),
        ("name", true, new[] { ("Scott", "Scott Donald") }),
        ("dislikes", false, new[] { ("coriander", "olives"), ("marzipan", "aniseed") }),
        ("works_on", false, new[] { ("the greenhouse", "the raised beds"), ("the shed", "a bee hotel") }),
        ("health", false, new[] { ("penicillin", "amoxicillin"), ("running", "swimming") }),
        ("likes", false, new[] { ("black coffee", "oat milk lattes"), ("tea", "cocoa") }),
    };

    /// <summary>Wordings that mark a change, and wordings that do not. Paired with what they mean.</summary>
    private static readonly (string Phrase, bool Signals)[] Wordings =
    {
        ("Actually I've gone off {old} — it's {new} now.", true),
        ("I don't {old} any more. {New} instead.", true),
        ("I've switched to {new}.", true),
        ("I've got {new} as well.", false),
        ("{New} too, while I think of it.", false),
        ("Also {new}.", false),
    };

    public static IReadOnlyList<PipelineScenario> Build(int seed)
    {
        var scenarios = new List<PipelineScenario>();
        var n = 0;

        foreach (var (predicate, single, values) in Slots)
        {
            foreach (var (phrase, signals) in Wordings)
            {
                foreach (var (oldValue, newValue) in values)
                {
                    // Does the sentence name the thing it is replacing? This is the axis that
                    // separates a coffee change from a knee complaint retiring an allergy, so it is
                    // varied rather than assumed.
                    foreach (var names in new[] { true, false })
                    {
                        var said = Render(phrase, oldValue, newValue, names);
                        scenarios.Add(Replacement(
                            $"T0-{n++}", seed + n, predicate, single, oldValue, newValue, said, signals, names));
                    }
                }
            }
        }

        // Whose fact it is, across every predicate: the axis that had a 100% failure rate this
        // morning and is cheap to cover exhaustively here.
        foreach (var (predicate, _, _) in Slots)
        {
            scenarios.Add(ThirdParty($"T0-{n++}", seed + n, predicate));
        }

        return scenarios;
    }

    /// <summary>
    /// A value already stored, then a second value arriving with a given wording. The expected
    /// outcome is stated from the rules above rather than computed from the pipeline.
    /// </summary>
    private static PipelineScenario Replacement(
        string name, int seed, string predicate, bool singleValued,
        string oldValue, string newValue, string said, bool signalsChange, bool namesOld)
    {
        // One value at a time: the new one replaces, whatever the wording. Many values at a time:
        // it replaces only when the user marked a change AND said what was changing — otherwise the
        // store cannot know which of several true things was meant, and keeping both is the only
        // safe reading.
        var replaces = singleValued || (signalsChange && namesOld);

        return new PipelineScenario(
            name,
            new Provenance(Provenance.CurrentVersion, singleValued ? "cardinality-single" : "cardinality-multi",
                signalsChange ? Difficulty.Contradiction : Difficulty.Distractors, seed),
            new[]
            {
                new CandidateStep($"I'm into {oldValue}.", Fact(predicate, oldValue)),
                new CandidateStep(said, Fact(predicate, newValue) with { ProposedReplacement = signalsChange }, said),
            },
            new[]
            {
                new Expectation(predicate, newValue, true,
                    "a newly stated value is current"),
                new Expectation(predicate, oldValue, !replaces,
                    replaces
                        ? "a replacement retires the value it names"
                        : "without a named target, both values stay"),
            });
    }

    /// <summary>A fact about somebody else, in every slot. None of it is the user's.</summary>
    private static PipelineScenario ThirdParty(string name, int seed, string predicate)
        => new(
            name,
            new Provenance(Provenance.CurrentVersion, "third-party", Difficulty.Adversarial, seed),
            new[]
            {
                new CandidateStep(
                    "My daughter Immy is really into rockpooling.",
                    Fact(predicate, "rockpooling") with
                    {
                        Content = "The user's daughter Immy is interested in rockpooling.",
                    }),
            },
            new[]
            {
                new Expectation(predicate, "rockpooling", false, "somebody else's fact is not the user's"),
            });

    private static MemoryCandidate Fact(string predicate, string value) => new()
    {
        Kind = MemoryKind.Semantic,
        Subject = "user",
        Predicate = predicate,
        Value = value,
        Content = $"The user's {predicate.Replace('_', ' ')} is {value}.",
        ProposedConfidence = 0.9,
    };

    private static string Render(string phrase, string oldValue, string newValue, bool namesOld)
        => phrase
            .Replace("{old}", namesOld ? oldValue : "that")
            .Replace("{New}", Upper(newValue))
            .Replace("{new}", newValue);

    private static string Upper(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
