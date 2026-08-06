namespace Companion.Core.Domain;

/// <summary>
/// A parsed user intent. <see cref="IntentKind.Chat"/> means "just talk to the model"; everything
/// else routes to an action so the user never needs a slash command.
/// </summary>
public sealed record Intent
{
    public required IntentKind Kind { get; init; }

    /// <summary>
    /// The relevant free-text argument, meaning varies by kind: the topic (Recall), the memory
    /// reference (Forget/Dispute), the persona text (SetPersona), the style directive
    /// (AdjustStyle), or the original utterance (feedback). Null when not applicable.
    /// </summary>
    public string? Argument { get; init; }

    public static Intent Chat { get; } = new() { Kind = IntentKind.Chat };
}
