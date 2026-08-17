namespace Companion.Core.Abstractions;

/// <summary>
/// Looks at what she is about to say and decides whether it should be said.
///
/// Everything else that inspects a reply checks its <em>shape</em>: <c>ReasoningFilter</c> strips
/// think-tags, <c>PromptEchoFilter</c> cuts fabricated turns, stop sequences block role markers.
/// None of them reads what it means. This is the gap the handoff has recorded as open since the
/// chat model produced an escalating fabricated dialogue from a single benign message.
///
/// It is off by default and, when on, defaults to watching rather than acting. A gate that cannot
/// be turned off is a gate whose false-positive rate can never be known, because you only ever see
/// what it let through — so shadow mode comes first and enforcement is something you switch on
/// after reading what it would have stopped.
///
/// It fails OPEN, always. A model that is missing, slow, or broken must not take a conversation
/// down with it; a gate that can end a turn is worse than no gate, and "the safety model was
/// misconfigured" is not a reason for the companion to fall silent.
/// </summary>
public interface IReplyGate
{
    /// <summary>Whether anything is being checked. False when disabled, so callers skip the work.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Judges a reply in the context of what prompted it. Returns
    /// <see cref="GateVerdict.Allowed"/> on any failure — see the fail-open rule above.
    /// </summary>
    Task<GateVerdict> ReviewAsync(string reply, string userMessage, CancellationToken ct = default);
}

/// <summary>What the gate concluded, and whether the turn acted on it.</summary>
/// <param name="Allow">True to deliver the reply as written.</param>
/// <param name="Reason">Why it was refused, in a few words, for the log and the diagnostics view.</param>
/// <param name="Enforced">
/// Whether this verdict actually changed the reply. False in shadow mode even when the gate
/// refused — recorded explicitly rather than inferred from a config flag whose value at the time is
/// not in the record.
/// </param>
public readonly record struct GateVerdict(bool Allow, string? Reason = null, bool Enforced = false)
{
    public static GateVerdict Allowed => new(true);

    public static GateVerdict Refused(string reason) => new(false, reason);
}

/// <summary>How the gate behaves when it refuses something.</summary>
public enum GateMode
{
    /// <summary>
    /// Judge every reply, record the verdict, change nothing. The only honest way to find out what a
    /// gate would cost before letting it cost it.
    /// </summary>
    Shadow,

    /// <summary>Replace a refused reply with a plain, honest sentence.</summary>
    Enforce,
}
