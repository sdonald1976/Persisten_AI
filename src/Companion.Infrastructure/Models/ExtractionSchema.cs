using Companion.Core.Abstractions;
using Companion.Core.Services;

namespace Companion.Infrastructure.Models;

/// <summary>
/// The JSON Schema the extraction model is decoded against.
///
/// Every field the pipeline later makes a decision on is an enum here rather than free text. That
/// is the whole point: deterministic code downstream used to key on words the model chose, and the
/// model chose differently for the same fact depending on phrasing. Constraining generation removes
/// the problem rather than defending against it — <c>drinks_coffee_black</c> is not a value that can
/// be produced, so nothing has to cope with it.
///
/// <c>replaces</c> is new, and it is the model being asked the question the pipeline actually needs
/// answered instead of one it will infer afterwards. The extractor is already reading the message;
/// whether the user is changing something or adding something is plain in it. The pipeline treats
/// the answer as a proposal — it still checks there is a plausible target and that the evidence
/// came from the user — so this joins <see cref="FactSupersession"/>'s reading of the user's own
/// words rather than replacing it. Two independent signals, neither trusted alone.
/// </summary>
internal static class ExtractionSchema
{
    public static ResponseFormat Format { get; } = ResponseFormat.FromSchema("memory_candidates", Build());

    private static object Build() => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new[] { "memories" },
        ["properties"] = new Dictionary<string, object?>
        {
            // An object wrapper rather than a bare array: JSON-mode providers reliably return an
            // object, and a top-level array is the shape small models get wrong most often.
            ["memories"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "Every durable memory in this exchange. Empty when there is none.",
                ["maxItems"] = 20,
                ["items"] = Item(),
            },
        },
    };

    private static object Item() => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        // predicate and value are required, not optional. Left optional the model simply omitted
        // them on half the memories in a real run, and the pipeline's fallback is the literal
        // predicate "fact" — which is one enormous multi-valued slot holding everything, i.e.
        // exactly the unbounded key space the vocabulary exists to remove. An enum nobody is
        // obliged to fill in constrains nothing. Episodic items carry a predicate too; it is
        // ignored for them, and that is cheaper than a conditional schema small models get wrong.
        // episodeStatus is required for the same reason as predicate: left optional the model omits
        // it, the fallback is "Occurred", and an unfinished job recorded as already having happened
        // opens no loop and is never asked about again. "I'm rebuilding the irrigation this
        // weekend" came back as Occurred on a real run.
        ["required"] = new[] { "kind", "content", "excerpt", "predicate", "value", "episodeStatus" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["kind"] = Enum(
                "semantic for a lasting fact; episodic for a specific event, including work that is "
                + "not finished yet",
                "semantic", "episodic"),

            ["content"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["maxLength"] = 400,
                ["description"] = "The memory as one plain sentence about the user.",
            },

            ["excerpt"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["maxLength"] = 400,
                ["description"] =
                    "The user's own words supporting this, quoted from their message. Must be "
                    + "something they STATED — a question they asked is not a statement of fact.",
            },

            ["subject"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["maxLength"] = 60,
                ["description"] = "Almost always \"user\". Semantic memories only.",
            },

            // The field the whole exercise is about.
            ["predicate"] = Enum(
                "Which relation this fact is, for semantic memories. Pick the closest; use "
                + "\"other\" only when none fits.\n" + PredicateVocabulary.Describe(),
                PredicateVocabulary.Names.ToArray()),

            ["value"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["maxLength"] = 200,
                ["description"] = "What the predicate holds — \"Scott\", \"oat milk lattes\", \"the greenhouse irrigation\".",
            },

            ["replaces"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] =
                    "True only when the user is CHANGING something they told you before "
                    + "(\"actually, I've gone off black coffee\"), false when they are adding "
                    + "something new — including a second project, a second pet, another dislike. "
                    + "If in doubt, false: adding is far more common than changing.",
            },

            ["episodeStatus"] = Enum(
                "When this happened, relative to now. Occurred = already done and over. Planned = "
                + "they intend to do it but have not started. InProgress = under way and NOT "
                + "finished. Resolved = they have just finished something they had outstanding. "
                + "Anything the user is still going to do, or is in the middle of, is Planned or "
                + "InProgress and never Occurred — this is how the companion knows to ask how it "
                + "went. On a semantic memory put Occurred; it is ignored there.",
                "Occurred", "Planned", "InProgress", "Resolved"),

            ["validity"] = Enum("Current unless the user says it is temporary or already past.",
                "Current", "Temporary", "Historical"),

            ["relatedProject"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["maxLength"] = 120,
                ["description"] =
                    "The name of the ongoing work this belongs to, exactly as the user writes it. "
                    + "Use it for every memory about that work, not only the first.",
            },

            ["importance"] = Unit("How much this matters to the user, 0 to 1."),
            ["confidence"] = Unit("How sure you are the user stated this, 0 to 1."),
        },
    };

    private static object Enum(string description, params string[] values)
        => new Dictionary<string, object?>
        {
            ["type"] = "string",
            ["enum"] = values,
            ["description"] = description,
        };

    private static object Unit(string description)
        => new Dictionary<string, object?>
        {
            ["type"] = "number",
            ["minimum"] = 0,
            ["maximum"] = 1,
            ["description"] = description,
        };
}
