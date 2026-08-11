namespace Companion.Core.Domain;

public sealed class Procedure
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public ProcedureOwner Owner { get; set; } = ProcedureOwner.UserTaught;
    public ProcedureAccess Access { get; set; } = ProcedureAccess.Explainable;
    public Guid? ProjectId { get; set; }
    public ProcedureStatus Status { get; set; } = ProcedureStatus.Active;
    public double Confidence { get; set; }
    public Guid SourceConversationId { get; set; }
    public Guid SourceMessageId { get; set; }
    public string Evidence { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<ProcedureStep> Steps { get; set; } = new List<ProcedureStep>();
    public ICollection<ProcedureRevision> Revisions { get; set; } = new List<ProcedureRevision>();
}

public sealed class ProcedureStep
{
    public Guid Id { get; set; }
    public Guid ProcedureId { get; set; }
    public string UserId { get; set; } = default!;
    public int Order { get; set; }
    public string Instruction { get; set; } = default!;
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProcedureRevision
{
    public Guid Id { get; set; }
    public Guid ProcedureId { get; set; }
    public string UserId { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
    public string Kind { get; set; } = default!;
    public string Note { get; set; } = default!;
    public Guid SourceMessageId { get; set; }
}
