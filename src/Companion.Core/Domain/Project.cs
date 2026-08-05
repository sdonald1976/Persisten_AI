namespace Companion.Core.Domain;

/// <summary>
/// A first-class project record. Rather than a loose collection of chat messages, a project
/// tracks its own identity, purpose, and status; its dynamic state (decisions, events, open
/// loops) hangs off it and is reconstructed into a <see cref="ProjectSummary"/> on demand.
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string? Purpose { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Embedding of the project's identity (name + aliases + purpose) for fuzzy resolution.</summary>
    public float[]? Embedding { get; set; }
}
