namespace Companion.Eval;

/// <summary>
/// Where an example came from and how it was made.
///
/// Kept on every generated life because training data without provenance is impossible to debug
/// later: when a model trained on this behaves oddly, the first question is always which generator,
/// which template, which seed — and a dataset that cannot answer that is a dataset that has to be
/// thrown away rather than corrected.
///
/// <see cref="Seed"/> plus <see cref="GeneratorVersion"/> is enough to reproduce a life exactly,
/// which is what turns a failure into something that can be fixed rather than something that was
/// seen once.
/// </summary>
/// <param name="GeneratorVersion">Bumped whenever the templates change meaningfully.</param>
/// <param name="Family">The scenario family — what this life is designed to probe.</param>
/// <param name="Difficulty">Curriculum level; see <see cref="Difficulty"/>.</param>
/// <param name="Seed">The exact seed for this life, so it can be regenerated alone.</param>
/// <param name="Source">Synthetic here; runtime capture and human review use the same field.</param>
public sealed record Provenance(
    string GeneratorVersion,
    string Family,
    Difficulty Difficulty,
    int Seed,
    string Source = "synthetic")
{
    /// <summary>
    /// Bump on any change to the templates or expectations. A dataset mixing two generator versions
    /// silently is one where a metric moved and nobody can say whether the model or the data changed.
    /// </summary>
    public const string CurrentVersion = "lives-2";

    public override string ToString()
        => $"{GeneratorVersion}/{Family}/L{(int)Difficulty}/seed{Seed}";
}

/// <summary>
/// How hard a life is meant to be, so a candidate model can be scored across the curriculum rather
/// than on an average that hides where it actually fails.
///
/// The point is diagnostic, not decorative: a policy that scores 95% overall while failing every
/// level 5 has not generalized, it has learned the easy cases — and a single accuracy number cannot
/// tell those apart.
/// </summary>
public enum Difficulty
{
    /// <summary>Stated plainly, once, with nothing nearby to confuse it.</summary>
    Obvious = 1,

    /// <summary>The same fact said in different words later. Recognising it is the whole test.</summary>
    Paraphrased = 2,

    /// <summary>Several similar facts in the same slot, only one of which is the target.</summary>
    Distractors = 3,

    /// <summary>Something the user changes their mind about.</summary>
    Contradiction = 4,

    /// <summary>A fact stated early, tested after many intervening messages.</summary>
    TemporalGap = 5,

    /// <summary>Written specifically to defeat the current rule. See the adversarial families.</summary>
    Adversarial = 6,
}
