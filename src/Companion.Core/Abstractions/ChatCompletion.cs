namespace Companion.Core.Abstractions;

/// <summary>
/// The result of a chat generation: the reply text plus the signals around it — why generation
/// stopped, how many rounds it took, and token usage. Callers that only want the answer read
/// <see cref="Text"/>; the metadata is what makes completion behaviour debuggable and lets the
/// turn record how each reply was actually produced (see the generation fields on
/// <see cref="Companion.Core.Domain.Message"/>).
/// </summary>
public sealed record ChatCompletion
{
    public required string Text { get; init; }

    /// <summary>
    /// The provider's stop reason for the final round: <c>"stop"</c> (the model finished on its
    /// own), <c>"length"</c> (it hit the output-token limit mid-answer), or another provider value.
    /// This is the transport-level truth about whether the reply was cut off.
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>Generation rounds: 1 for a single call, more when auto-continuation ran.</summary>
    public int Rounds { get; init; } = 1;

    /// <summary>
    /// True when generation stopped while the reply was still being cut off by the token limit —
    /// i.e. the continuation cap was reached before the model finished naturally.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The model name the server reported for this generation (or the configured one).</summary>
    public string? Model { get; init; }

    /// <summary>Prompt/input tokens the server billed, if it reported usage.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion/output tokens the server billed, if it reported usage.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>A one-shot completion from plain text (used by deterministic/offline models).</summary>
    public static ChatCompletion FromText(string text, string? model = null) =>
        new() { Text = text, FinishReason = "stop", Model = model };
}
