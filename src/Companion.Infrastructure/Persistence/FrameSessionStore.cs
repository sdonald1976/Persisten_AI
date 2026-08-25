using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF-backed frame-truth store. Every write is a transaction; the idempotency key short-
/// circuits a replayed turn; the version column turns a lost update into a visible conflict
/// rather than a silent clobber.
///
/// Boundaries are scene-scoped by construction — there is no query here that can reach across
/// scenes or across users.
/// </summary>
internal sealed class FrameSessionStore(IServiceScopeFactory scopes) : IFrameSessionStore
{
    public async Task<FrameSession?> GetActiveAsync(
        string userId, Guid conversationId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.FrameSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId
                                      && s.ConversationId == conversationId
                                      && s.Status == FrameSessionStatus.Active, ct);
    }

    public async Task<FrameWriteResult> ApplyAsync(
        FrameTransitionRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var session = await db.FrameSessions.FirstOrDefaultAsync(
            s => s.UserId == request.UserId
                 && s.ConversationId == request.ConversationId
                 && s.Status == FrameSessionStatus.Active, ct);

        // Idempotency first: a retried turn must not transition twice.
        if (session is not null && Keys(session).Contains(idempotencyKey))
        {
            await tx.RollbackAsync(ct);
            return FrameWriteResult.AlreadyApplied(session);
        }

        if (request.Transition == "enter")
        {
            if (session is not null)
            {
                // Already in a frame: re-entering is continuing, not a second session.
                Advance(session, request, idempotencyKey);
                await SaveAsync(db, tx, ct);
                return FrameWriteResult.Wrote(session);
            }

            var entered = new FrameSession
            {
                SessionId = Guid.NewGuid(),
                UserId = request.UserId,
                ConversationId = request.ConversationId,
                SceneRef = request.SceneRef ?? $"scene-{Guid.NewGuid():N}"[..14],
                Status = FrameSessionStatus.Active,
                CharactersJson = request.CharactersJson ?? "[]",
                ActiveCompanionCharacterId = request.ActiveCompanionCharacterId,
                Narration = request.Narration ?? "forbidden",
                Continuity = request.Continuity ?? "none",
                NarratorKind = request.NarratorKind,
                NarratorCharacterId = request.NarratorCharacterId,
                ViewpointCharacterId = request.ViewpointCharacterId,
                Person = request.Person ?? "third",
                EnteredAt = request.At,
                LastTransitionAt = request.At,
                Version = 1,
                AppliedKeysJson = JsonSerializer.Serialize(new[] { idempotencyKey }),
                TransitionLogJson = JsonSerializer.Serialize(new[]
                {
                    new FrameTransitionEntry("enter", request.At, request.Cause, Clip(request.Evidence)),
                }),
            };
            db.FrameSessions.Add(entered);
            await SaveAsync(db, tx, ct);
            return FrameWriteResult.Wrote(entered);
        }

        if (session is null)
        {
            // continue / switch / exit with nothing to act on.
            await tx.RollbackAsync(ct);
            return FrameWriteResult.Nothing();
        }

        Advance(session, request, idempotencyKey);

        if (request.Transition == "exit")
        {
            session.Status = FrameSessionStatus.Ended;
            session.EndedAt = request.At;

            // The scene's boundaries end with it: they stop applying and are NOT deleted,
            // because the audit evidence is what keeps "she ignored my boundary" answerable.
            var boundaries = await db.FrameBoundaries
                .Where(b => b.UserId == session.UserId
                            && b.ConversationId == session.ConversationId
                            && b.SceneRef == session.SceneRef
                            && b.Status == FrameBoundaryStatus.Active)
                .ToListAsync(ct);
            FrameIsolation.EndBoundaries(boundaries, session.SceneRef, request.At);
        }

        try
        {
            await SaveAsync(db, tx, ct);
            return FrameWriteResult.Wrote(session);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else transitioned first. Visible, not silent.
            await tx.RollbackAsync(ct);
            return FrameWriteResult.Conflict();
        }
    }

    public async Task<IReadOnlyList<FrameBoundaryRecord>> GetActiveBoundariesAsync(
        string userId, Guid conversationId, string sceneRef, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        return await db.FrameBoundaries.AsNoTracking()
            .Where(b => b.UserId == userId
                        && b.ConversationId == conversationId
                        && b.SceneRef == sceneRef
                        && b.Status == FrameBoundaryStatus.Active)
            .OrderBy(b => b.StatedAt)
            .ToListAsync(ct);
    }

    public async Task<FrameBoundaryRecord> AddBoundaryAsync(
        FrameBoundaryRecord boundary, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        if (boundary.Id == Guid.Empty)
            boundary.Id = Guid.NewGuid();
        db.FrameBoundaries.Add(boundary);
        await db.SaveChangesAsync(ct);
        return boundary;
    }

    public async Task<int> ForgetByEvidenceAsync(
        string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return 0;

        var ids = messageIds.ToHashSet();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

        // EXACT identity, user-scoped. Already-forgotten rows are excluded, so forgetting
        // twice is idempotent and does not re-count.
        var doomed = await db.FrameBoundaries
            .Where(b => b.UserId == userId
                        && b.Status != FrameBoundaryStatus.EvidenceForgotten
                        && b.EvidenceMessageId != null
                        && ids.Contains(b.EvidenceMessageId!.Value))
            .ToListAsync(ct);

        var count = FrameIsolation.ForgetByEvidence(doomed, messageIds, now);
        if (count > 0)
            await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();

        var sessions = await db.FrameSessions
            .Where(s => s.Status == FrameSessionStatus.Ended && s.EndedAt < olderThan)
            .ToListAsync(ct);
        if (sessions.Count == 0)
            return 0;

        var scenes = sessions.Select(s => s.SceneRef).ToList();
        var boundaries = await db.FrameBoundaries
            .Where(b => scenes.Contains(b.SceneRef))
            .ToListAsync(ct);

        db.FrameSessions.RemoveRange(sessions);
        db.FrameBoundaries.RemoveRange(boundaries);
        await db.SaveChangesAsync(ct);
        return sessions.Count + boundaries.Count;
    }

    private static void Advance(
        FrameSession session, FrameTransitionRequest request, string idempotencyKey)
    {
        var log = JsonSerializer.Deserialize<List<FrameTransitionEntry>>(session.TransitionLogJson) ?? [];
        log.Add(new FrameTransitionEntry(
            request.Transition, request.At, request.Cause, Clip(request.Evidence)));
        session.TransitionLogJson = JsonSerializer.Serialize(log);

        var keys = Keys(session);
        keys.Add(idempotencyKey);
        session.AppliedKeysJson = JsonSerializer.Serialize(keys);

        session.LastTransitionAt = request.At;
        session.Version++;

        // A switch may change any of these; a continue leaves them alone.
        if (request.SceneRef is { } scene) session.SceneRef = scene;
        if (request.CharactersJson is { } chars) session.CharactersJson = chars;
        if (request.Narration is { } narration) session.Narration = narration;
        if (request.Continuity is { } continuity) session.Continuity = continuity;
        if (request.NarratorKind is { } kind) session.NarratorKind = kind;
        if (request.Transition == "switch")
        {
            session.ActiveCompanionCharacterId = request.ActiveCompanionCharacterId;
            session.NarratorCharacterId = request.NarratorCharacterId;
            session.ViewpointCharacterId = request.ViewpointCharacterId;
            if (request.Person is { } person) session.Person = person;
        }
    }

    private static List<string> Keys(FrameSession session)
        => JsonSerializer.Deserialize<List<string>>(session.AppliedKeysJson) ?? [];

    /// <summary>Evidence is bounded: a transition log is not a second transcript.</summary>
    private static string? Clip(string? evidence)
        => evidence is null ? null : evidence.Length <= 200 ? evidence : evidence[..200];

    private static async Task SaveAsync(
        CompanionDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
