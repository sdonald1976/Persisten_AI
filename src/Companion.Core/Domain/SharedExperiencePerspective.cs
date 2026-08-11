namespace Companion.Core.Domain;

public sealed class SharedExperiencePerspective
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid ExperienceId { get; set; }
    public MemoryOwner Owner { get; set; }
    public string Summary { get; set; } = default!;
    public double Confidence { get; set; }
    public string Evidence { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public int Revision { get; set; } = 1;
}
