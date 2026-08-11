using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

public interface IProcedureStore
{
    Task<Procedure?> AddOrUpdateFromTeachingAsync(string userId, Guid conversationId, Message message, DateTimeOffset now, CancellationToken ct = default);
    Task<Procedure?> ApplyRevisionAsync(string userId, Message message, DateTimeOffset now, CancellationToken ct = default);
    Task<IReadOnlyList<Procedure>> SearchAsync(string userId, string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<ProcedureRevision>> GetRevisionsAsync(string userId, Guid procedureId, CancellationToken ct = default);
}
