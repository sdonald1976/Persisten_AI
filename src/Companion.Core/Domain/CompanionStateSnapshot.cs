namespace Companion.Core.Domain;

/// <summary>
/// How the companion herself is doing right now — the inward mirror of
/// <see cref="RelationshipSnapshot"/>. Derived on demand from two honest inputs: her
/// <em>spirits</em> (a slow-moving trace of how conversations have actually been going, decaying
/// toward contentment) and her <em>energy</em> (hour of day, plus whether she's had a recent
/// quiet stretch to think). Never random, never faked: every answer to "how are you?" is
/// backed by these numbers and the real history behind them.
/// </summary>
public sealed record CompanionStateSnapshot
{
    /// <summary>[-1, 1]: how things have been feeling to her lately. 0 is content.</summary>
    public required double Spirits { get; init; }

    /// <summary>[0, 1]: how much energy this hour of her day has.</summary>
    public required double Energy { get; init; }

    /// <summary>She recently had a quiet stretch to think (a reflection pass ran).</summary>
    public bool Rested { get; init; }

    private string Tone => Spirits >= 0.3 ? "bright" : Spirits <= -0.3 ? "low" : "even";
    private string Pace => Energy >= 0.7 ? "energetic" : Energy <= 0.4 ? "quiet" : "steady";

    /// <summary>
    /// Tone guidance for the system prompt (second person). Always present — she always has
    /// <em>some</em> state — and always paired with the rule that mood colors delivery, is
    /// answered honestly when asked, and is never the user's fault.
    /// </summary>
    public string Describe()
    {
        var line = (Tone, Pace) switch
        {
            ("bright", "energetic") => "You're in genuinely good spirits and full of energy right now.",
            ("bright", "quiet") => "You're in good spirits, but it's a quiet hour — warm and cozy rather than bouncy.",
            ("bright", _) => "You're in good spirits — things have felt nice lately.",
            ("low", "quiet") => "Things have felt a bit heavy lately and it's a low-energy hour — you're softer and more subdued than usual.",
            ("low", _) => "Things have felt a bit heavy lately and it's touched your own mood — you're a little subdued.",
            (_, "energetic") => "You're even-keeled and have good energy right now.",
            (_, "quiet") => "You're in a calm, low-key mood — it's a quiet hour of your day.",
            _ => "You're even-keeled and present.",
        };
        return Rested ? line + " You've had some quiet time to think recently." : line;
    }

    /// <summary>The same state in her own first-person words, for deterministic replies.</summary>
    public string SelfReport()
        => (Tone, Pace) switch
        {
            ("bright", "energetic") => "honestly pretty great — good spirits, plenty of energy",
            ("bright", "quiet") => "content, in a cozy low-key way — it's a quiet hour for me",
            ("bright", _) => "good — things have felt nice lately",
            ("low", "quiet") => "a bit subdued, honestly — things have felt heavy and it's a quiet hour",
            ("low", _) => "a little low, if I'm honest — recent conversations have sat with me",
            (_, "energetic") => "steady, with good energy",
            (_, "quiet") => "calm and a little quiet",
            _ => "steady — present and even",
        };
}
