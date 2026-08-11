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

    /// <summary>Editable persona/style instructions, prepended to the system prompt.</summary>
    public string? Persona { get; init; }

    /// <summary>Recent verbatim turns, oldest first.</summary>
    public IReadOnlyList<Message> RecentMessages { get; init; } = Array.Empty<Message>();

    /// <summary>Memories selected for this turn (already ranked and budget-limited).</summary>
    public IReadOnlyList<ContextItem> Memories { get; init; } = Array.Empty<ContextItem>();

    /// <summary>State of the project this turn resolved to, if any.</summary>
    public ProjectSummary? Project { get; init; }

    /// <summary>Open loops relevant to this turn.</summary>
    public IReadOnlyList<RetrievedOpenLoop> OpenLoops { get; init; } = Array.Empty<RetrievedOpenLoop>();

    /// <summary>A clarifying question to ask when the project reference was ambiguous.</summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>
    /// One line of tone guidance from the relational/emotional layer ("the user has seemed stressed
    /// lately…"), or null. It shapes how the companion responds — never something to state back.
    /// </summary>
    public string? RelationshipNote { get; init; }

    /// <summary>
    /// A recent private musing from the companion's between-session reflection — its own thought,
    /// not a fact about the user. Rendered under an explicit "hold loosely, never read back" label;
    /// it colors attention and continuity ("I was thinking about what you said…"), nothing more.
    /// </summary>
    public string? Musing { get; init; }

    /// <summary>
    /// A held curiosity offered to this turn — a question the companion genuinely wonders about.
    /// The model is told to raise it at most once and only when it fits naturally; injecting it
    /// consumes it (it is marked voiced), so it can never become a nag.
    /// </summary>
    public string? CuriosityQuestion { get; init; }

    /// <summary>
    /// The companion's OWN mood right now (from her inner state — spirits + energy). Colors
    /// delivery; answered honestly if the user asks how she is; never announced unprompted and
    /// never the user's fault.
    /// </summary>
    public string? MoodNote { get; init; }

    /// <summary>
    /// Reply-shape guidance derived from the user's message ("short and casual — match it"), or
    /// null when standing long-form rules govern. The fix for essay-length replies to "lol fair".
    /// </summary>
    public string? RegisterNote { get; init; }

    /// <summary>
    /// Where the relationship actually is (tenure + real interaction depth) — calibrates how
    /// casual, teasing, and shorthand she's allowed to be. Never recited back.
    /// </summary>
    public string? FamiliarityNote { get; init; }

    /// <summary>
    /// Compact temporal grounding — what day/time it is and how long since you last spoke — so
    /// "back already?" vs "look who finally showed up" emerges from context, never a script.
    /// </summary>
    public string? TemporalNote { get; init; }

    /// <summary>
    /// The companion's OWN relevant tastes for this turn (natural language, never raw numbers) —
    /// hers alone, so honest agreement and honest disagreement are both possible.
    /// </summary>
    public IReadOnlyList<string> PreferenceNotes { get; init; } = Array.Empty<string>();

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

    /// <summary>Whose story the line is from — shared moments render as "remember when we…".</summary>
    public MemoryOwner Owner { get; init; } = MemoryOwner.User;
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
