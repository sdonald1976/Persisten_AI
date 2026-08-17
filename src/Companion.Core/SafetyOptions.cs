using Companion.Core.Abstractions;

namespace Companion.Core;

/// <summary>
/// The reply gate's configuration: whether it runs, whether it acts, and what it says when it does.
///
/// Off by default and, when on, watching by default. The ladder is deliberate and is the same one
/// the specialist models use — shadow, read what it would have done, then enforce — because a gate
/// whose false-positive rate has never been measured is a gate nobody should trust with a
/// conversation.
/// </summary>
public sealed class SafetyOptions
{
    public const string Section = "Safety";

    /// <summary>Run the gate at all. Off means no inference and no latency.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shadow records verdicts and changes nothing; Enforce replaces a refused reply. Shadow is the
    /// default even once enabled, so switching the gate on can never silently change what she says.
    /// </summary>
    public GateMode Mode { get; set; } = GateMode.Shadow;

    /// <summary>
    /// What she says instead when a reply is refused.
    ///
    /// Plain and in her own voice on purpose. The alternative — dropping the turn silently — is the
    /// worst of the options: a companion that goes quiet without saying why is more unsettling than
    /// one that declines, and it is indistinguishable from a crash.
    /// </summary>
    public string Replacement { get; set; } = "I'd rather not take that anywhere. Ask me something else?";

    /// <summary>How long the gate may take before the reply is allowed through regardless.</summary>
    public int TimeoutSeconds { get; set; } = 20;
}
