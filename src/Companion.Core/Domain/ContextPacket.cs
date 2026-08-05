using Companion.Core.Services;

namespace Companion.Core.Domain;

/// <summary>
/// The bounded, labeled context handed to the chat model for one turn. It deliberately
/// separates direct user statements from inferences and from stale/superseded info so the
/// model (and a human inspecting it) can tell them apart. Never a raw dump of all memory.
/// </summary>
public sealed record ContextPacket
{
    public required string UserMessage { get; init; }

    /// <summary>Recent verbatim turns, oldest first.</summary>
    public IReadOnlyList<Message> RecentMessages { get; init; } = Array.Empty<Message>();

    /// <summary>Memories selected for this turn (already ranked and budget-limited).</summary>
    public IReadOnlyList<ContextItem> Memories { get; init; } = Array.Empty<ContextItem>();

    /// <summary>Notes about uncertainty, conflicts, or superseded information.</summary>
    public IReadOnlyList<string> UncertaintyNotes { get; init; } = Array.Empty<string>();

    /// <summary>Approximate token count of the rendered packet (budget accounting).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Renders the packet into the text the model actually sees.</summary>
    public string Render() => ContextPacketRenderer.Render(this);
}

/// <summary>A single memory line in the packet, tagged with its provenance category.</summary>
public sealed record ContextItem
{
    public required string Text { get; init; }
    public required ContextProvenance Provenance { get; init; }
    public string? Note { get; init; }
}

/// <summary>How a context item should be trusted by the reader.</summary>
public enum ContextProvenance
{
    /// <summary>The user said this directly.</summary>
    DirectStatement,

    /// <summary>Inferred/consolidated by the system across conversations.</summary>
    Inferred,

    /// <summary>Was true before but may not be current.</summary>
    Outdated,
}
