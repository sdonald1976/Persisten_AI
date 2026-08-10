namespace Companion.Core.Domain;

/// <summary>
/// A session opener the companion offers so the user never faces a blank prompt. It leads with a
/// low-pressure message and a few concrete, memory-grounded starters (open loops, recent
/// projects) the user can pick — or ignore entirely and just talk.
/// </summary>
public sealed record Greeting
{
    public required string Message { get; init; }

    /// <summary>
    /// How long it's been since the user last talked, as a natural phrase ("3 days"), or null when
    /// it's their first time or they only just left. Lets a face — or the model-written greeting —
    /// acknowledge the gap ("it's been 3 days").
    /// </summary>
    public string? TimeContext { get; init; }

    /// <summary>Concrete things to pick up, drawn from what the companion actually remembers.</summary>
    public IReadOnlyList<string> Openers { get; init; } = Array.Empty<string>();

    /// <summary>The greeting as one block of text: the message, then the openers as a bullet list.</summary>
    public string ToDisplayText()
        => Openers.Count == 0
            ? Message
            : Message + "\n\n" + string.Join("\n", Openers.Select(o => "  • " + o));
}
