namespace Companion.Core.Domain;

/// <summary>Why a reflection pass produced nothing.</summary>
public enum ReflectionSkipReason
{
    /// <summary>It didn't skip — <see cref="ReflectionOutcome.Result"/> is present.</summary>
    None = 0,

    /// <summary>Reflection is switched off in configuration.</summary>
    Disabled,

    /// <summary>Not enough new conversation since the last pass to be worth thinking about.</summary>
    NotEnoughMaterial,

    /// <summary>The model answered, but not with anything parseable — nothing was stored.</summary>
    ModelOutputUnusable,
}

/// <summary>
/// The result of one reflection pass, or why there wasn't one.
///
/// The distinction matters to whoever is looking: "nothing new to think about" is the system
/// working, while "the model returned garbage" is a broken model or prompt that will keep failing
/// silently. Collapsing both into a null return hid the second behind the first.
/// </summary>
public sealed record ReflectionOutcome
{
    public ReflectionResult? Result { get; init; }

    public ReflectionSkipReason SkipReason { get; init; }

    /// <summary>True when the pass ran and produced a stored reflection.</summary>
    public bool Reflected => Result is not null;

    public static ReflectionOutcome From(ReflectionResult result) =>
        new() { Result = result, SkipReason = ReflectionSkipReason.None };

    public static ReflectionOutcome Skipped(ReflectionSkipReason reason) =>
        new() { SkipReason = reason };
}
