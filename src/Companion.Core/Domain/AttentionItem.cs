namespace Companion.Core.Domain;

public sealed class AttentionItem
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string Summary { get; set; } = default!;
    public AttentionSourceType SourceType { get; set; } = AttentionSourceType.Conversation;
    public string? SourceId { get; set; }
    public MemoryOwner Owner { get; set; } = MemoryOwner.Shared;
    public double Strength { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public AttentionStatus Status { get; set; } = AttentionStatus.Active;
}
