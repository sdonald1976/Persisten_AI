using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// EF persistence for concept knowledge, mirroring MemoryStore's disciplines: evidence rows
/// persisted with every assertion, a MemoryRevision per lifecycle change, write-through to
/// the vector index so retrieval stays in step with the tables.
/// </summary>
public sealed class ConceptStore : IConceptStore
{
    private readonly CompanionDbContext _db;
    private readonly IVectorIndexMaintenance _vectorIndex;
    private readonly TimeProvider _clock;

    public ConceptStore(CompanionDbContext db, IVectorIndexMaintenance vectorIndex, TimeProvider clock)
    {
        _db = db;
        _vectorIndex = vectorIndex;
        _clock = clock;
    }

    public async Task<Concept?> FindByNameAsync(
        string userId, string canonicalName, CancellationToken ct = default)
    {
        var concept = await _db.Concepts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.CanonicalName == canonicalName, ct);
        if (concept is not null)
            return concept;

        var alias = await _db.ConceptAliases.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Alias == canonicalName, ct);
        return alias is null
            ? null
            : await _db.Concepts.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == alias.ConceptId && c.UserId == userId, ct);
    }

    public async Task AddConceptAsync(Concept concept, CancellationToken ct = default)
    {
        _db.Concepts.Add(concept);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConceptAssertion>> GetAssertionsAsync(
        string userId, Guid conceptId, CancellationToken ct = default)
        => await _db.ConceptAssertions.AsNoTracking()
            .Where(a => a.UserId == userId && a.ConceptId == conceptId
                && a.Status != MemoryStatus.Deleted)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAssertionAsync(ConceptAssertion assertion, CancellationToken ct = default)
    {
        _db.ConceptAssertions.Add(assertion);
        foreach (var evidence in assertion.Evidence)
            _db.Evidence.Add(evidence);
        _db.Revisions.Add(Revision(assertion, RevisionKind.Created,
            $"Learned ({assertion.Origin.ToKebab()}, confidence {assertion.Confidence:F2}).",
            after: assertion.NormalizedText));
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(assertion);
    }

    public async Task ConfirmAssertionAsync(
        ConceptAssertion assertion, double confidence, DateTimeOffset now, CancellationToken ct = default)
    {
        var tracked = await _db.ConceptAssertions
            .FirstAsync(a => a.Id == assertion.Id && a.UserId == assertion.UserId, ct);
        var before = $"confidence={tracked.Confidence:F2}, lastConfirmed={tracked.LastConfirmed:o}";
        tracked.Confidence = Math.Max(tracked.Confidence,
            Core.Services.ConfidenceCalculator.Compute(confidence, true, 1));
        tracked.LastConfirmed = now;
        _db.Revisions.Add(Revision(tracked, RevisionKind.Confirmed,
            "Re-taught unchanged.", before,
            $"confidence={tracked.Confidence:F2}, lastConfirmed={now:o}"));
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(tracked);
    }

    public async Task SupersedeAssertionAsync(
        ConceptAssertion old, ConceptAssertion replacement, CancellationToken ct = default)
    {
        var tracked = await _db.ConceptAssertions
            .FirstAsync(a => a.Id == old.Id && a.UserId == old.UserId, ct);
        tracked.Validity = Validity.Superseded;
        tracked.Status = MemoryStatus.Superseded;
        tracked.SupersededById = replacement.Id;
        // Embeddings are derived and regenerable; dropping this one is what removes the
        // superseded definition from the index (Sync removes anything embedding-less).
        tracked.Embedding = null;
        _db.Revisions.Add(Revision(tracked, RevisionKind.Superseded,
            "Re-taught with a different definition.",
            before: tracked.NormalizedText, after: replacement.NormalizedText));
        await _db.SaveChangesAsync(ct);
        _vectorIndex.Sync(tracked); // embedding-less → removed from the index

        await AddAssertionAsync(replacement, ct);
    }

    private MemoryRevision Revision(
        ConceptAssertion assertion, RevisionKind kind, string note,
        string? before = null, string? after = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = assertion.UserId,
        MemoryId = assertion.Id,
        MemoryKind = MemoryKind.Concept,
        Kind = kind,
        Timestamp = _clock.GetUtcNow(),
        Actor = "concept-knowledge",
        Note = note,
        Before = before,
        After = after,
    };
}
