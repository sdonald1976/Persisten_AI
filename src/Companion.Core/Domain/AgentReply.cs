namespace Companion.Core.Domain;

/// <summary>What kind of reply the agent produced for a single utterance.</summary>
public enum AgentReplyKind
{
    /// <summary>A normal conversational turn (the model generated the reply).</summary>
    Chat,

    /// <summary>A recognized intent was carried out (recall, persona change, feedback…).</summary>
    Action,

    /// <summary>
    /// A destructive action needs a yes/no before it happens (e.g. "forget that"). The face
    /// confirms however it likes — a CLI prompt, a spoken "yes", a button — then calls
    /// <c>IAgent.ConfirmAsync</c> with <see cref="AgentReply.ConfirmationToken"/>.
    /// </summary>
    Confirmation,
}

/// <summary>
/// The result of handling one user utterance, as structured data the "face" (CLI, HTTP, voice,
/// avatar) renders however it wants. This is the boundary of the brain/face split: the brain
/// decides <em>what</em> to say and do; the face decides <em>how</em> to present it.
/// </summary>
public sealed record AgentReply
{
    public required AgentReplyKind Kind { get; init; }

    /// <summary>The intent the utterance was understood as.</summary>
    public required IntentKind Intent { get; init; }

    /// <summary>The natural-language reply to speak or print.</summary>
    public required string Text { get; init; }

    /// <summary>Full per-turn diagnostics — present only for <see cref="AgentReplyKind.Chat"/>.</summary>
    public TurnTrace? Trace { get; init; }

    /// <summary>
    /// Opaque token to pass back to <c>IAgent.ConfirmAsync</c> when
    /// <see cref="Kind"/> is <see cref="AgentReplyKind.Confirmation"/>.
    /// </summary>
    public string? ConfirmationToken { get; init; }

    public static AgentReply Act(IntentKind intent, string text) =>
        new() { Kind = AgentReplyKind.Action, Intent = intent, Text = text };
}
