using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// Temporal revision and corrections. Every operation preserves history and writes an audit
/// entry: supersession keeps the old fact (marked not-current), deletion is soft and nulls the
/// embedding so it can't resurface via similarity, and disputes demote a memory from retrieval.
/// </summary>
public sealed class MemoryCurator : IMemoryCurator
{
    private readonly IMemoryStore _memories;
    private readonly IEmbeddingModel _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<MemoryCurator> _logger;

    public MemoryCurator(
        IMemoryStore memories,
        IEmbeddingModel embeddings,
        TimeProvider clock,
        ILogger<MemoryCurator> logger)
    {
        _memories = memories;
        _embeddings = embeddings;
        _clock = clock;
        _logger = logger;
    }

    public async Task SupersedeSemanticAsync(
        string userId, Guid oldId, SemanticMemory replacement, string reason, CancellationToken ct = default)
    {
        var old = await _memories.GetSemanticAsync(oldId, userId, ct);
        if (old is null)
            throw new InvalidOperationException($"Semantic memory {oldId} not found for user.");

        var before = $"status={old.Status}, validity={old.Validity}";
        old.Status = MemoryStatus.Superseded;
        old.Validity = Validity.Superseded;
        old.SupersededById = replacement.Id;
        await _memories.UpdateSemanticAsync(old, ct);

        replacement.UserId = userId;
        replacement.Status = MemoryStatus.Active;
        await _memories.AddSemanticAsync(replacement, ct);

        await RevisionAsync(userId, old.Id, MemoryKind.Semantic, RevisionKind.Superseded, reason,
            before, $"superseded by {replacement.Id}", ct);
        await RevisionAsync(userId, replacement.Id, MemoryKind.Semantic, RevisionKind.Created,
            $"Supersedes {old.Id}: {reason}", old.NormalizedFact, replacement.NormalizedFact, ct);

        _logger.LogInformation("Superseded semantic {Old} with {New} for {User}", old.Id, replacement.Id, userId);
    }

    public async Task<bool> CorrectSemanticAsync(
        string userId, Guid id, string newValue, string newNormalizedFact, CancellationToken ct = default)
    {
        var m = await _memories.GetSemanticAsync(id, userId, ct);
        if (m is null)
            return false;

        var before = m.NormalizedFact;
        m.Value = newValue;
        m.NormalizedFact = newNormalizedFact;
        m.Embedding = await _embeddings.EmbedAsync(newNormalizedFact, ct); // keep the index consistent
        await _memories.UpdateSemanticAsync(m, ct);

        await RevisionAsync(userId, m.Id, MemoryKind.Semantic, RevisionKind.Updated,
            "Corrected by user.", before, newNormalizedFact, ct);
        return true;
    }

    public async Task<bool> ReassignMemoryProjectAsync(
        string userId, Guid memoryId, string? newProject, CancellationToken ct = default)
    {
        var (sem, epi) = await FindAsync(memoryId, userId, ct);
        if (sem is not null)
        {
            var before = sem.RelatedProject ?? "(none)";
            sem.RelatedProject = newProject;
            await _memories.UpdateSemanticAsync(sem, ct);
            await RevisionAsync(userId, sem.Id, MemoryKind.Semantic, RevisionKind.Updated,
                "Re-associated project.", before, newProject ?? "(none)", ct);
            return true;
        }
        if (epi is not null)
        {
            var before = epi.RelatedProject ?? "(none)";
            epi.RelatedProject = newProject;
            await _memories.UpdateEpisodicAsync(epi, ct);
            await RevisionAsync(userId, epi.Id, MemoryKind.Episodic, RevisionKind.Updated,
                "Re-associated project.", before, newProject ?? "(none)", ct);
            return true;
        }
        return false;
    }

    public async Task<bool> ForgetAsync(string userId, Guid memoryId, string reason, CancellationToken ct = default)
        => await SetStatusAsync(userId, memoryId, MemoryStatus.Deleted, RevisionKind.Deleted, reason, purgeEmbedding: true, ct);

    public async Task<bool> DisputeAsync(string userId, Guid memoryId, string reason, CancellationToken ct = default)
        => await SetStatusAsync(userId, memoryId, MemoryStatus.Disputed, RevisionKind.Disputed, reason, purgeEmbedding: false, ct);

    public async Task<bool> MergeAsync(string userId, Guid sourceId, Guid targetId, CancellationToken ct = default)
    {
        var (sourceSem, sourceEpi) = await FindAsync(sourceId, userId, ct);
        var (targetSem, targetEpi) = await FindAsync(targetId, userId, ct);
        var targetKind = targetSem is not null ? MemoryKind.Semantic
            : targetEpi is not null ? MemoryKind.Episodic : (MemoryKind?)null;
        if (targetKind is null || (sourceSem is null && sourceEpi is null))
            return false;

        // Move the source's evidence onto the target so provenance is preserved.
        var evidence = await _memories.GetEvidenceAsync(userId, sourceId, ct);
        if (evidence.Count > 0)
        {
            await _memories.AddEvidenceAsync(userId, evidence.Select(e => new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                MemoryId = targetId,
                MemoryKind = targetKind.Value,
                MessageId = e.MessageId,
                Excerpt = e.Excerpt,
                Weight = e.Weight,
            }).ToList(), ct);
        }

        await SetStatusAsync(userId, sourceId, MemoryStatus.Deleted, RevisionKind.Merged,
            $"Merged into {targetId}.", purgeEmbedding: true, ct);
        await RevisionAsync(userId, targetId, targetKind.Value, RevisionKind.Merged,
            $"Absorbed {sourceId}.", null, null, ct);
        return true;
    }

    public async Task<bool> ResolveReviewAsync(
        string userId, Guid candidateId, bool accept, CancellationToken ct = default)
    {
        var candidate = await _memories.GetSemanticAsync(candidateId, userId, ct);
        if (candidate is null || candidate.Status != MemoryStatus.Candidate)
            return false;

        if (!accept)
        {
            candidate.Status = MemoryStatus.Deleted;
            candidate.Embedding = null;
            await _memories.UpdateSemanticAsync(candidate, ct);
            await RevisionAsync(userId, candidate.Id, MemoryKind.Semantic, RevisionKind.Deleted,
                "Review rejected.", null, null, ct);
            return true;
        }

        // Accept: supersede any active fact occupying the same slot, then promote the candidate.
        var slot = MemoryNormalizer.SemanticSlotKey(candidate.Subject, candidate.Predicate);
        var conflicting = (await _memories.GetRetrievableMemoriesAsync(userId, ct))
            .OfType<SemanticMemory>()
            .FirstOrDefault(m => m.Id != candidate.Id
                && m.Status == MemoryStatus.Active
                && MemoryNormalizer.SemanticSlotKey(m.Subject, m.Predicate) == slot);

        if (conflicting is not null)
        {
            conflicting.Status = MemoryStatus.Superseded;
            conflicting.Validity = Validity.Superseded;
            conflicting.SupersededById = candidate.Id;
            await _memories.UpdateSemanticAsync(conflicting, ct);
            await RevisionAsync(userId, conflicting.Id, MemoryKind.Semantic, RevisionKind.Superseded,
                "Superseded on review acceptance.", conflicting.NormalizedFact, candidate.NormalizedFact, ct);
        }

        candidate.Status = MemoryStatus.Active;
        candidate.LastConfirmed = _clock.GetUtcNow();
        await _memories.UpdateSemanticAsync(candidate, ct);
        await RevisionAsync(userId, candidate.Id, MemoryKind.Semantic, RevisionKind.Confirmed,
            "Promoted from review.", null, candidate.NormalizedFact, ct);
        return true;
    }

    private async Task<bool> SetStatusAsync(
        string userId, Guid memoryId, MemoryStatus status, RevisionKind revision,
        string reason, bool purgeEmbedding, CancellationToken ct)
    {
        var (sem, epi) = await FindAsync(memoryId, userId, ct);
        if (sem is not null)
        {
            var before = sem.Status.ToString();
            sem.Status = status;
            if (purgeEmbedding) sem.Embedding = null;
            await _memories.UpdateSemanticAsync(sem, ct);
            await RevisionAsync(userId, sem.Id, MemoryKind.Semantic, revision, reason, before, status.ToString(), ct);
            return true;
        }
        if (epi is not null)
        {
            var before = epi.Status.ToString();
            epi.Status = status;
            if (purgeEmbedding) epi.Embedding = null;
            await _memories.UpdateEpisodicAsync(epi, ct);
            await RevisionAsync(userId, epi.Id, MemoryKind.Episodic, revision, reason, before, status.ToString(), ct);
            return true;
        }
        return false;
    }

    private async Task<(SemanticMemory? sem, EpisodicMemory? epi)> FindAsync(
        Guid id, string userId, CancellationToken ct)
    {
        var sem = await _memories.GetSemanticAsync(id, userId, ct);
        if (sem is not null)
            return (sem, null);
        return (null, await _memories.GetEpisodicAsync(id, userId, ct));
    }

    private Task RevisionAsync(
        string userId, Guid memoryId, MemoryKind kind, RevisionKind revision, string note,
        string? before, string? after, CancellationToken ct)
        => _memories.AddRevisionAsync(userId, new MemoryRevision
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MemoryId = memoryId,
            MemoryKind = kind,
            Kind = revision,
            Timestamp = _clock.GetUtcNow(),
            Actor = "user",
            Note = note,
            Before = before,
            After = after,
        }, ct);
}
