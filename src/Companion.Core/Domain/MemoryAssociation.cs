namespace Companion.Core.Domain;

public sealed class MemoryAssociation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid SourceMemoryId { get; set; }
    public Guid TargetMemoryId { get; set; }
    public MemoryAssociationType AssociationType { get; set; } = MemoryAssociationType.TopicRelated;
    public double Strength { get; set; }
    public string Evidence { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastReinforcedAt { get; set; }
}
