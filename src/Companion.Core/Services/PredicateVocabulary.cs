namespace Companion.Core.Services;

/// <summary>
/// The closed set of relations a semantic memory may hold, and which of them can hold more than one
/// value at a time.
///
/// It exists because the extraction model used to invent the predicate, and deterministic code
/// downstream was keyed on what it invented. That is the single defect behind most of what went
/// wrong with this store: "I drink my coffee black" produced <c>drinks_coffee_black</c> and the
/// later "oat milk lattes now" produced <c>prefers</c>, so the two never met and both stayed
/// current; meanwhile two unrelated projects both landed in <c>works_on</c> and the second deleted
/// the first. Every fix for that was code defending itself against an unbounded key space.
///
/// So the key space is bounded instead. This list is handed to the model as a JSON-schema enum,
/// which the provider enforces during decoding — <c>drinks_coffee_black</c> becomes impossible to
/// emit rather than something to cope with. Slot keys are then stable across models, prompts,
/// temperatures and reruns, and cardinality becomes a property of a known finite set rather than a
/// guess about a string.
///
/// <see cref="Other"/> is the escape hatch, and it is deliberately single-valued=false and
/// deliberately vague: a companion meets facts nobody anticipated, and a vocabulary with no way to
/// say "something else" quietly discards them. Facts that keep arriving as <c>other</c> are the
/// signal that this list needs another entry.
///
/// Memories written before this existed keep their free-text predicates and still work: lookups
/// canonicalize, and anything unrecognized is treated as multi-valued, which is the safe default —
/// wrongly keeping two facts is untidy, wrongly dropping one destroys something the user said.
///
/// One consequence to hold on to, because it is the cost of the trade rather than a bug in it: a
/// misclassification no longer invents a harmless new slot, it lands in an existing one. Asked
/// about "a second allotment plot at Marsh Lane", qwen2.5:7b chose <c>lives_in</c> — a valid value
/// for the wrong relation, where before it would have coined something nobody else used. Junk in an
/// unused slot is inert; junk in a single-valued slot displaces a real fact. That is why the
/// pipeline holds single-valued supersession to the higher similarity bar, and why entries are only
/// marked single-valued when they genuinely admit one value.
/// </summary>
public static class PredicateVocabulary
{
    /// <param name="Name">The wire value, and the slot key component.</param>
    /// <param name="SingleValued">True when a new value necessarily displaces the old one.</param>
    /// <param name="Meaning">Shown to the model in the schema so it picks the right one.</param>
    public sealed record Entry(string Name, bool SingleValued, string Meaning);

    /// <summary>The catch-all for a real fact that fits nothing else.</summary>
    public const string Other = "other";

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        // --- one value at a time: a new one replaces the old without being announced ---
        new Entry("name", true, "what the user is called"),
        new Entry("age", true, "the user's age"),
        new Entry("birthday", true, "the user's date of birth"),
        new Entry("lives_in", true, "where the user currently lives"),
        new Entry("hometown", true, "where the user is from or grew up"),
        new Entry("nationality", true, "the user's nationality"),
        new Entry("timezone", true, "the user's timezone"),
        new Entry("gender", true, "the user's gender"),
        new Entry("pronouns", true, "the user's pronouns"),
        new Entry("occupation", true, "the user's job or profession"),
        new Entry("employer", true, "who the user works for"),
        new Entry("relationship_status", true, "single, married, partnered and so on"),
        new Entry("diet", true, "the user's current way of eating, as a whole"),

        // --- many at once: a new value joins the old ones ---
        new Entry("works_on", false, "a project or piece of ongoing work"),
        new Entry("likes", false, "something the user enjoys or prefers"),
        new Entry("dislikes", false, "something the user dislikes or avoids"),
        new Entry("interested_in", false, "a lasting interest or subject they follow"),
        new Entry("skilled_at", false, "something the user can do well"),
        new Entry("learning", false, "something the user is studying or picking up"),
        new Entry("speaks_language", false, "a language the user speaks"),
        new Entry("owns", false, "a possession worth remembering"),
        new Entry("has_pet", false, "an animal the user keeps"),
        new Entry("relationship", false, "a person in the user's life and who they are to them"),
        new Entry("goal", false, "something the user is aiming for"),
        new Entry("routine", false, "a regular habit or rhythm of the user's"),
        new Entry("health", false, "a lasting health condition, allergy or constraint"),
        new Entry("belief", false, "a value or view the user holds"),
        // There is deliberately no separate "dislikes_doing": with both present, qwen2.5:7b filed
        // "I can't stand coriander" under it. Two entries a person would struggle to choose between
        // are two ways to spell one slot, and the whole value of a closed set is that the same fact
        // lands in the same place every time.

        new Entry(Other, false, "a durable fact that genuinely fits none of the above"),
    };

    /// <summary>The wire values, for the schema enum.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(e => e.Name).ToArray();

    private static readonly Dictionary<string, Entry> ByName =
        All.ToDictionary(e => e.Name, StringComparer.Ordinal);

    /// <summary>True when this predicate holds one value at a time. Unknown predicates are not.</summary>
    public static bool IsSingleValued(string? predicate)
        => predicate is not null
            && ByName.TryGetValue(Canonical(predicate), out var entry)
            && entry.SingleValued;

    /// <summary>True when the predicate is one this vocabulary actually defines.</summary>
    public static bool IsKnown(string? predicate)
        => predicate is not null && ByName.ContainsKey(Canonical(predicate));

    /// <summary>
    /// Lowercased, punctuation-and-spacing-insensitive form, so "Lives In", "lives-in" and
    /// "lives_in" are one slot. Returns the input's canonical form whether or not it is known —
    /// legacy predicates must keep matching each other.
    /// </summary>
    public static string Canonical(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate))
            return string.Empty;

        var chars = predicate.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    /// <summary>The enum values plus their meanings, for the model-facing schema description.</summary>
    public static string Describe()
        => string.Join("\n", All.Select(e => $"  {e.Name} — {e.Meaning}"));
}
