namespace Companion.Core.Domain;

/// <summary>
/// An alternate way the user refers to a project ("the mind project", "that AI thing").
/// Aliases are evidence for entity resolution and carry their own confidence so uncertain
/// aliases don't cause silent mis-resolution.
/// </summary>
public class ProjectAlias
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string UserId { get; set; } = default!;

    public string Alias { get; set; } = default!;
    public string? Source { get; set; }
    public double Confidence { get; set; } = 1.0;
}
