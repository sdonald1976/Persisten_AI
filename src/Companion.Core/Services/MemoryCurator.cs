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
    private readonly IShadowRecorder? _shadow;
    private readonly IUserPreferenceStore? _userPreferences;
    private readonly IEmotionStore? _emotions;
    private readonly ICompanionMoodLog? _moodLog;
    private readonly ICompanionStateTracker? _innerState;
    private readonly IFrameSessionStore? _frames;
    private readonly IExperienceStore? _experiences;
    private readonly IReflectionStore? _reflections;
    private readonly IAttentionStore? _attention;
    private readonly IPreferenceStore? _companionPreferences;
    private readonly ISharedPerspectiveStore? _perspectives;
    private readonly IGapStore? _gaps;
    private readonly IDiagnosticsStore? _diagnostics;

    public MemoryCurator(
        IMemoryStore memories,
        IEmbeddingModel embeddings,
        TimeProvider clock,
        ILogger<MemoryCurator> logger,
        IShadowRecorder? shadow = null,
        IUserPreferenceStore? userPreferences = null,
        IEmotionStore? emotions = null,
        ICompanionMoodLog? moodLog = null,
        ICompanionStateTracker? innerState = null,
        IFrameSessionStore? frames = null,
        IExperienceStore? experiences = null,
        IReflectionStore? reflections = null,
        IAttentionStore? attention = null,
        IPreferenceStore? companionPreferences = null,
        ISharedPerspectiveStore? perspectives = null,
        IGapStore? gaps = null,
        IDiagnosticsStore? diagnostics = null)
    {
        _memories = memories;
        _embeddings = embeddings;
        _clock = clock;
        _logger = logger;

        // Optional, like every other measurement seam here: the curator's job is the memory, and a
        // telemetry table that is not switched on must not be something it has to have.
        _shadow = shadow;
        _userPreferences = userPreferences;
        _emotions = emotions;
        _moodLog = moodLog;
        _innerState = innerState;
        _frames = frames;
        _experiences = experiences;
        _reflections = reflections;
        _attention = attention;
        _companionPreferences = companionPreferences;
        _perspectives = perspectives;
        _gaps = gaps;
        _diagnostics = diagnostics;
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
    {
        // The excerpts are read BEFORE the status changes, because they are the only handle on the
        // sentences this memory came from and nothing guarantees they stay reachable afterwards.
        // Read unconditionally now: preference invalidation (Source 3) needs the same handles
        // whether or not the shadow recorder is on.
        var evidence = await _memories.GetEvidenceAsync(userId, memoryId, ct);
        var evidenceMessageIds = evidence.Select(e => e.MessageId).Distinct().ToList();
        List<string> excerpts = evidence.Select(e => e.Excerpt).ToList();
        if (_shadow is { IsRecording: true })
        {

            // The id too: pair-capture rows (memory.supersession.pair) reference the stored memory
            // by id rather than by its evidence — the excerpts in a pair row are the user's words
            // for the INCOMING fact, so forgetting the stored one would never match them. A guid
            // string is 36 characters, comfortably over the minimum-excerpt bar, and cannot
            // collide with conversational text.
            excerpts.Add(memoryId.ToString());
        }

        var forgotten = await SetStatusAsync(
            userId, memoryId, MemoryStatus.Deleted, RevisionKind.Deleted, reason,
            purgeEmbedding: true, ct);

        // And the captured sentences go with it. Capture's gate is evaluated at turn time — private,
        // in-character, off the record — which covers everything except changing your mind later,
        // and changing your mind later is what /forget IS. Without this the memory is deleted, its
        // embedding purged, and the sentence stays in the corpus table as training data.
        if (forgotten && excerpts.Count > 0 && _shadow is not null)
        {
            var removed = await _shadow.ForgetCapturesAsync(excerpts, ct);
            if (removed > 0)
                _logger.LogInformation(
                    "Forgetting {MemoryId} also removed {Count} captured sentences.", memoryId, removed);
        }

        // Source 3: a preference whose authority depended on THIS evidence loses it now —
        // deactivated (EvidenceForgotten) and its statement purged.
        //
        // Linkage is by exact identity: the evidence message ids above, or a forgotten
        // excerpt that EQUALS an instruction verbatim. Never containment — an unrelated
        // memory that merely shares a phrase with a standing instruction must not be able
        // to revoke it. Where a statement matches more than one active preference the
        // association is ambiguous, and ambiguity revokes nothing: picking one of two
        // identical instructions would be a guess, and a guess that silently drops a
        // user's standing rule is the worst kind.
        if (forgotten && _userPreferences is not null)
        {
            var result = await _userPreferences.InvalidateByForgottenEvidenceAsync(
                userId, excerpts, evidenceMessageIds, _clock.GetUtcNow(), ct);
            if (result.Invalidated > 0)
                _logger.LogInformation(
                    "Forgetting {MemoryId} also invalidated {Count} user preferences.",
                    memoryId, result.Invalidated);
            if (result.Ambiguous > 0)
                _logger.LogWarning(
                    "Forgetting {MemoryId} matched {Count} ambiguous preference association(s); "
                    + "none were revoked. Revoke the instruction explicitly to clear it.",
                    memoryId, result.Ambiguous);
        }

        // Phase 0: the emotional readings taken from those same messages lose their evidence
        // too. Matched by EXACT id only — a signal is never redacted because forgotten text
        // resembled its cue phrase. The row survives as metadata; the user's words do not.
        if (forgotten && _emotions is not null && evidenceMessageIds.Count > 0)
        {
            // Read the affected signals FIRST: their evidence event ids are the handle the
            // mood log needs, and redaction is about to remove everything else.
            var affected = (await _emotions.GetRecentSignalsAsync(userId, int.MaxValue, ct))
                .Where(s => evidenceMessageIds.Contains(s.MessageId))
                .Select(s => s.EvidenceEventId)
                .ToList();

            var redacted = await _emotions.ForgetByEvidenceAsync(
                userId, evidenceMessageIds, [], _clock.GetUtcNow(), ct);
            if (redacted > 0)
                _logger.LogInformation(
                    "Forgetting {MemoryId} also redacted {Count} emotional signal(s).", memoryId, redacted);

            // And her mood log is COMPACTED past those moments. Not rewound — she was
            // affected, and forgetting the record of it does not undo that — but every row
            // from which the forgotten valence could be recomputed is replaced by one opaque
            // baseline carrying where she actually stands.
            if (_moodLog is not null && _innerState is not null && affected.Count > 0)
            {
                var spirits = (await _innerState.BuildAsync(userId, ct)).Spirits;
                var compaction = await _moodLog.CompactForgottenAsync(
                    userId, affected, spirits, _clock.GetUtcNow(), ct);
                if (compaction.Compacted)
                    _logger.LogInformation(
                        "Forgetting {MemoryId} compacted {Count} mood transition(s) behind a "
                        + "baseline at version {Version}; replay before it is unavailable by design.",
                        memoryId, compaction.RowsRemoved, compaction.BaselineVersion);
            }
        }

        // R-01: and the frame record. A frame transition names the turn that caused it, and
        // a scene-scoped boundary names the turn that stated it. Both are exact message ids,
        // so both are severable by the same identity rule the rest of this method uses — and
        // both were unreachable from here until now, which meant /forget was quietly partial.
        if (forgotten && _frames is not null && evidenceMessageIds.Count > 0)
        {
            var severed = await _frames.ForgetByEvidenceAsync(
                userId, evidenceMessageIds, _clock.GetUtcNow(), ct);
            if (severed > 0)
                _logger.LogInformation(
                    "Forgetting {MemoryId} also severed {Count} frame evidence link(s).",
                    memoryId, severed);
        }

        // A1: the derived record types. Each carries its own documented outcome -- redact,
        // delete, or sever-and-recompute -- stated on EvidenceForgetting rather than decided
        // here, so the policy lives with the rule instead of at the call site.
        //
        // Order is load-bearing in exactly one place: a shared perspective follows the
        // experience it comments on, so it must be resolved BEFORE that experience is
        // redacted and its lineage stops being readable.
        if (forgotten && evidenceMessageIds.Count > 0)
        {
            var at = _clock.GetUtcNow();
            await SweepAsync("shared perspectives",
                _perspectives is null ? null : (u, m, n, c) => _perspectives.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("experiences",
                _experiences is null ? null : (u, m, n, c) => _experiences.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("reflections and curiosities",
                _reflections is null ? null : (u, m, n, c) => _reflections.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("attention items",
                _attention is null ? null : (u, m, n, c) => _attention.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("companion preferences",
                _companionPreferences is null ? null : (u, m, n, c) => _companionPreferences.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("knowledge gaps",
                _gaps is null ? null : (u, m, n, c) => _gaps.ForgetByEvidenceAsync(u, m, n, c));
            await SweepAsync("turn diagnostics",
                _diagnostics is null ? null : (u, m, n, c) => _diagnostics.ForgetByEvidenceAsync(u, m, n, c));

            async Task SweepAsync(
                string what,
                Func<string, IReadOnlyCollection<Guid>, DateTimeOffset, CancellationToken, Task<int>>? sweep)
            {
                if (sweep is null)
                    return;
                var affected = await sweep(userId, evidenceMessageIds, at, ct);
                if (affected > 0)
                    _logger.LogInformation(
                        "Forgetting {MemoryId} also cleared {Count} {Kind}.", memoryId, affected, what);
            }
        }

        return forgotten;
    }

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
