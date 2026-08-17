using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Validates extracted candidates into persisted memory. Extraction proposes; this pipeline
/// disposes: generate → normalize → dedupe (in-batch) → compare to existing → score
/// confidence → require evidence → decide (accept / merge / reject / needs-review) → persist
/// with an audit trail. The model never writes memory directly.
/// </summary>
public sealed class MemoryPipeline : IMemoryPipeline
{
    private readonly IMemoryExtractor _extractor;
    private readonly IMemoryStore _store;
    private readonly IMemoryCurator _curator;
    private readonly IEmbeddingModel _embeddings;
    private readonly IProfileStore _profiles;
    private readonly IPersonalityService _personality;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<MemoryPipeline> _logger;

    // Optional, and both default to the "not here" implementations. Shadow comparison is a
    // measurement that must never change a decision, so the pipeline has to work identically with
    // neither of these present — which is also how every existing test constructs it.
    private readonly IShadowRecorder? _shadow;
    private readonly INliModel? _nli;

    public MemoryPipeline(
        IMemoryExtractor extractor,
        IMemoryStore store,
        IMemoryCurator curator,
        IEmbeddingModel embeddings,
        IProfileStore profiles,
        IPersonalityService personality,
        IOptions<CompanionOptions> options,
        TimeProvider clock,
        ILogger<MemoryPipeline> logger,
        IShadowRecorder? shadow = null,
        INliModel? nli = null)
    {
        _extractor = extractor;
        _store = store;
        _curator = curator;
        _embeddings = embeddings;
        _profiles = profiles;
        _personality = personality;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _shadow = shadow;
        _nli = nli;
    }

    public async Task<MemoryExtractionResult> ProcessAsync(
        string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default)
    {
        // 1. Generate.
        var raw = await _extractor.ExtractAsync(userId, exchange, ct);
        if (raw.Count == 0)
            return MemoryExtractionResult.Empty;

        // 2. Normalize + 3. de-duplicate within this batch.
        var batch = DedupeBatch(raw
            .Select(MemoryNormalizer.Normalize)
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .ToList());

        // 4. Load existing memory to compare against.
        var existing = await _store.GetRetrievableMemoriesAsync(userId, ct);
        var existingSemantic = existing.OfType<SemanticMemory>().ToList();
        var existingEpisodic = existing.OfType<EpisodicMemory>().ToList();

        var userMessageIds = exchange.Where(m => m.Role == MessageRole.User).Select(m => m.Id).ToHashSet();
        var validMessageIds = exchange.Select(m => m.Id).ToHashSet();
        var messageText = exchange
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First().Content ?? string.Empty);

        // The user's own words this turn, for the deterministic reads that need the phrasing rather
        // than the extracted fact — chiefly whether a new value replaces an old one or joins it.
        var userSaid = exchange
            .Where(m => m.Role == MessageRole.User)
            .Select(m => m.Content ?? string.Empty)
            .ToList();

        // Persona guard, layered under the turn-level in-character gate: a candidate that
        // references the companion herself (her name, or a relationship the persona claims) is a
        // fact about the CHARACTER, not the user's life — the fact store never learns fiction.
        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var lexicon = PersonaLexicon.From(
            _personality.Identity(profile).Name, _personality.Compose(profile));

        var decisions = new List<MemoryDecision>(batch.Count);
        foreach (var candidate in batch)
        {
            // Privacy guard: never persist obvious credentials, even if the model proposed them.
            if (LooksLikeSecret(candidate))
            {
                _logger.LogWarning("Rejected a candidate memory for {UserId} that looks like a credential.", userId);
                decisions.Add(Reject(candidate, "looks like a credential — not stored"));
                continue;
            }

            if (lexicon.MentionsCompanion(candidate.Content) || lexicon.MentionsCompanion(candidate.Value))
            {
                _logger.LogInformation(
                    "Rejected a candidate memory for {UserId} that references the companion's persona.", userId);
                decisions.Add(Reject(candidate, "references the companion's persona — in-character, not biography"));
                continue;
            }

            var decision = candidate.Kind == MemoryKind.Semantic
                ? await ProcessSemanticAsync(
                    userId, candidate, existingSemantic, userMessageIds, validMessageIds, messageText, userSaid, ct)
                : await ProcessEpisodicAsync(
                    userId, candidate, existingEpisodic, userMessageIds, validMessageIds, messageText, ct);
            decisions.Add(decision);
        }

        _logger.LogInformation(
            "Extraction for {UserId}: {Accepted} accepted, {Merged} merged, {Review} for review, {Rejected} rejected",
            userId, decisions.Count(d => d.Outcome == MemoryDecisionKind.Accepted),
            decisions.Count(d => d.Outcome == MemoryDecisionKind.Merged),
            decisions.Count(d => d.Outcome == MemoryDecisionKind.NeedsReview),
            decisions.Count(d => d.Outcome == MemoryDecisionKind.Rejected));

        return new MemoryExtractionResult { Decisions = decisions };
    }

    private async Task<MemoryDecision> ProcessSemanticAsync(
        string userId, MemoryCandidate candidate, List<SemanticMemory> existing,
        HashSet<Guid> userMessageIds, HashSet<Guid> validMessageIds,
        IReadOnlyDictionary<Guid, string> messageText, IReadOnlyList<string> userSaid, CancellationToken ct)
    {
        // 6. Require evidence traceable to a real message in this exchange...
        var evidence = ValidEvidence(candidate, validMessageIds);
        if (evidence.Count == 0)
            return Reject(candidate, "no valid source evidence");

        // ...and require that the user asserted it, rather than merely saying the words. A question
        // contains its own presupposition, so an honestly-cited excerpt can still be a fact the user
        // never stated.
        var asserted = AssertedEvidence(evidence, messageText);
        if (asserted.Count == 0)
            return Reject(candidate, AssertionGuard.Explain(evidence[0].Excerpt, Text(messageText, evidence[0])));
        evidence = asserted;

        var fromUser = evidence.Any(e => userMessageIds.Contains(e.MessageId));
        var embedding = await _embeddings.EmbedAsync(candidate.Content, ct);

        var valueKey = MemoryNormalizer.SemanticValueKey(candidate.Subject, candidate.Predicate, candidate.Value);
        var exact = existing.FirstOrDefault(m =>
            MemoryNormalizer.SemanticValueKey(m.Subject, m.Predicate, m.Value) == valueKey);

        var (nearest, similarity) = BestMatch(existing, embedding);

        // Same fact restated → confirmation (merge, don't duplicate).
        if (exact is not null)
            return await ConfirmSemanticAsync(exact, candidate, evidence, fromUser, ct);
        if (nearest is not null && similarity >= _options.DuplicateSimilarityThreshold)
            return await ConfirmSemanticAsync(nearest, candidate, evidence, fromUser, ct);

        // Does this new value REPLACE an existing one, or join it? See FactSupersession — the short
        // version is that a slot match plus similarity cannot tell those apart, so the question is
        // decided by whether the predicate can hold more than one value and by whether the user
        // said they were changing something.
        var slotKey = MemoryNormalizer.SemanticSlotKey(candidate.Subject, candidate.Predicate);
        var slotMatches = existing
            .Where(m => MemoryNormalizer.SemanticSlotKey(m.Subject, m.Predicate) == slotKey)
            .ToList();
        var (slotBest, slotSim) = BestMatch(slotMatches, embedding);

        // A single-valued predicate — a name, a birthday, where they live — holds one value by
        // definition, so a new one displaces the old whether or not they flagged the change. It
        // has to, because the extractor only sees this exchange: someone saying "I live in
        // Cambridge" has no way of signalling that Norwich was ever stored.
        //
        // The bar is the higher of the two thresholds, and that is a direct consequence of closing
        // the predicate vocabulary. A model that misreads a fact can no longer invent a slot for
        // it; it picks the nearest allowed one, and if that lands on a single-valued slot the
        // mistake now costs an existing memory instead of sitting harmlessly beside it. Asked to
        // classify "a second allotment plot at Marsh Lane", qwen2.5:7b answered `lives_in`. So
        // "these really are the same fact" is held to the replacement bar, not the looser
        // same-topic one. Supersession is still non-destructive — the old value is kept as
        // history, linked, with a revision — but a wrong one is a lie the user has to notice.
        var singleValuedBar = Math.Max(
            _options.ContradictionSimilarityThreshold, _options.ReplacementSimilarityThreshold);
        if (slotBest is not null
            && slotSim >= singleValuedBar
            && FactSupersession.IsSingleValued(candidate.Predicate))
        {
            return fromUser
                ? await SupersedeSemanticAsync(userId, candidate, embedding, evidence, slotBest, "a single-valued fact", ct)
                : await NeedsReviewSemanticAsync(userId, candidate, embedding, evidence, slotBest, fromUser, ct);
        }

        // Anything else replaces only when this turn actually says so — either the extractor read
        // it as a change, or the user's own wording marks one. Two independent readings of the same
        // question, and either is enough to look, because the guards below (a plausible target, a
        // similarity floor, and evidence from the user) are what decide whether anything happens.
        var wordingSaysReplace = candidate.ProposedReplacement
            || FactSupersession.SignalsReplacement(candidate.Evidence.Select(e => e.Excerpt), userSaid);

        // The one shadow comparison wired into the live path: what an entailment model would have
        // said about this same "replace or join?" question. Recorded, never acted on — the decision
        // below is unchanged by it. Measured on a hand-written set the model loses badly (0.462 to
        // the heuristic's 0.667), and the whole reason to record it here is that a set somebody
        // wrote down is not a conversation.
        await RecordSupersessionShadowAsync(candidate, nearest, wordingSaysReplace, ct);

        if (wordingSaysReplace)
        {
            var (replaced, replacedSim) = slotBest is not null && slotSim >= _options.ReplacementSimilarityThreshold
                ? (slotBest, slotSim)
                : (nearest, similarity);

            if (replaced is not null && replacedSim >= _options.ReplacementSimilarityThreshold)
            {
                return fromUser
                    ? await SupersedeSemanticAsync(
                        userId, candidate, embedding, evidence, replaced, "the user said this replaces it", ct)
                    : await NeedsReviewSemanticAsync(userId, candidate, embedding, evidence, replaced, fromUser, ct);
            }
        }

        // 5. Otherwise a new fact — score confidence and accept if it clears the bar.
        var confidence = ConfidenceCalculator.Compute(candidate.ProposedConfidence, fromUser, corroborations: 0);
        if (confidence < _options.MinAcceptConfidence)
            return Reject(candidate, $"confidence {confidence:F2} below threshold {_options.MinAcceptConfidence:F2}", confidence);

        return await AcceptSemanticAsync(userId, candidate, embedding, evidence, confidence, ct);
    }

    /// <summary>
    /// Asks the NLI model the same question the wording signal just answered, and files the pair.
    /// Costs nothing when shadow mode is off: the recorder reports it is not recording and the
    /// model is never run, because an inference whose answer nobody reads is pure latency.
    /// </summary>
    private async Task RecordSupersessionShadowAsync(
        MemoryCandidate candidate, SemanticMemory? nearest, bool wordingSaysReplace, CancellationToken ct)
    {
        if (_shadow is not { IsRecording: true } || _nli is not { IsAvailable: true } || nearest is null)
            return;

        var premise = nearest.NormalizedFact;
        await Shadow.CompareAsync<bool>(
            _shadow,
            "supersession.replaces",
            wordingSaysReplace,
            async token =>
            {
                var verdict = await _nli.ClassifyAsync(premise, candidate.Content, token);
                return (verdict.Label == Entailment.Contradiction, verdict.Confidence);
            },
            input: $"{premise} || {candidate.Content}",
            ct: ct);
    }

    private async Task<MemoryDecision> ProcessEpisodicAsync(
        string userId, MemoryCandidate candidate, List<EpisodicMemory> existing,
        HashSet<Guid> userMessageIds, HashSet<Guid> validMessageIds,
        IReadOnlyDictionary<Guid, string> messageText, CancellationToken ct)
    {
        var evidence = ValidEvidence(candidate, validMessageIds);
        if (evidence.Count == 0)
            return Reject(candidate, "no valid source evidence");

        var asserted = AssertedEvidence(evidence, messageText);
        if (asserted.Count == 0)
            return Reject(candidate, AssertionGuard.Explain(evidence[0].Excerpt, Text(messageText, evidence[0])));
        evidence = asserted;

        var fromUser = evidence.Any(e => userMessageIds.Contains(e.MessageId));
        var embedding = await _embeddings.EmbedAsync(candidate.Content, ct);

        var key = MemoryNormalizer.EpisodicKey(candidate.Content);
        var exact = existing.FirstOrDefault(m => MemoryNormalizer.EpisodicKey(m.Description) == key);
        var (nearest, similarity) = BestMatch(existing, embedding);

        if (exact is not null)
            return await ConfirmEpisodicAsync(exact, candidate, evidence, fromUser, ct);
        if (nearest is not null && similarity >= _options.DuplicateSimilarityThreshold)
            return await ConfirmEpisodicAsync(nearest, candidate, evidence, fromUser, ct);

        var confidence = ConfidenceCalculator.Compute(candidate.ProposedConfidence, fromUser, corroborations: 0);
        if (confidence < _options.MinAcceptConfidence)
            return Reject(candidate, $"confidence {confidence:F2} below threshold {_options.MinAcceptConfidence:F2}", confidence);

        return await AcceptEpisodicAsync(userId, candidate, embedding, evidence, confidence, ct);
    }

    // ---- persistence steps (8) ----

    private async Task<MemoryDecision> AcceptSemanticAsync(
        string userId, MemoryCandidate c, float[] embedding, List<MemoryEvidence> evidence,
        double confidence, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = c.Subject ?? "user",
            Predicate = c.Predicate ?? "fact",
            Value = c.Value ?? c.Content,
            NormalizedFact = c.Content,
            Confidence = confidence,
            Importance = c.Importance,
            Validity = c.Validity,
            Status = MemoryStatus.Active,
            FirstObserved = now,
            LastConfirmed = now,
            CreatedAt = now,
            RelatedProject = c.RelatedProject,
            Embedding = embedding,
        };
        AttachEvidence(memory.Evidence, evidence, memory.Id, MemoryKind.Semantic, userId);
        await _store.AddSemanticAsync(memory, ct);
        await WriteRevisionAsync(userId, memory.Id, MemoryKind.Semantic, RevisionKind.Created,
            $"Accepted new fact (confidence {confidence:F2}).", after: memory.NormalizedFact, ct: ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Accepted,
            Reason = "new fact accepted",
            FinalConfidence = confidence,
            ResultingMemoryId = memory.Id,
        };
    }

    private async Task<MemoryDecision> AcceptEpisodicAsync(
        string userId, MemoryCandidate c, float[] embedding, List<MemoryEvidence> evidence,
        double confidence, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var memory = new EpisodicMemory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Description = c.Content,
            EventTime = c.EventTime ?? now,
            TimePrecision = c.TimePrecision,
            MentionedAt = now,
            CreatedAt = now,
            EpisodeStatus = c.EpisodeStatus,
            Importance = c.Importance,
            Confidence = confidence,
            Status = MemoryStatus.Active,
            RelatedProject = c.RelatedProject,
            Embedding = embedding,
        };
        AttachEvidence(memory.Evidence, evidence, memory.Id, MemoryKind.Episodic, userId);
        await _store.AddEpisodicAsync(memory, ct);
        await WriteRevisionAsync(userId, memory.Id, MemoryKind.Episodic, RevisionKind.Created,
            $"Accepted new event (confidence {confidence:F2}).", after: memory.Description, ct: ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Accepted,
            Reason = "new event accepted",
            FinalConfidence = confidence,
            ResultingMemoryId = memory.Id,
        };
    }

    private async Task<MemoryDecision> ConfirmSemanticAsync(
        SemanticMemory target, MemoryCandidate c, List<MemoryEvidence> evidence, bool fromUser, CancellationToken ct)
    {
        var confidence = ConfidenceCalculator.Compute(c.ProposedConfidence, fromUser, corroborations: 1);
        var newConfidence = Math.Max(target.Confidence, confidence);
        var before = $"confidence={target.Confidence:F2}, lastConfirmed={target.LastConfirmed:o}";

        target.Confidence = newConfidence;
        target.LastConfirmed = _clock.GetUtcNow();

        await AddMergeEvidenceAsync(target.UserId, evidence, target.Id, MemoryKind.Semantic, ct);
        await _store.UpdateSemanticAsync(target, ct);
        await WriteRevisionAsync(target.UserId, target.Id, MemoryKind.Semantic, RevisionKind.Confirmed,
            "Confirmed existing fact from a new mention.", before,
            $"confidence={newConfidence:F2}, lastConfirmed={target.LastConfirmed:o}", ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Merged,
            Reason = "duplicate of an existing fact — confirmed rather than re-stored",
            FinalConfidence = newConfidence,
            ResultingMemoryId = target.Id,
            MatchedMemoryId = target.Id,
        };
    }

    private async Task<MemoryDecision> ConfirmEpisodicAsync(
        EpisodicMemory target, MemoryCandidate c, List<MemoryEvidence> evidence, bool fromUser, CancellationToken ct)
    {
        var confidence = ConfidenceCalculator.Compute(c.ProposedConfidence, fromUser, corroborations: 1);
        var newConfidence = Math.Max(target.Confidence, confidence);
        var before = $"confidence={target.Confidence:F2}, mentionedAt={target.MentionedAt:o}";

        target.Confidence = newConfidence;
        target.MentionedAt = _clock.GetUtcNow();

        await AddMergeEvidenceAsync(target.UserId, evidence, target.Id, MemoryKind.Episodic, ct);
        await _store.UpdateEpisodicAsync(target, ct);
        await WriteRevisionAsync(target.UserId, target.Id, MemoryKind.Episodic, RevisionKind.Confirmed,
            "Confirmed existing event from a new mention.", before,
            $"confidence={newConfidence:F2}, mentionedAt={target.MentionedAt:o}", ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Merged,
            Reason = "duplicate of an existing event — confirmed rather than re-stored",
            FinalConfidence = newConfidence,
            ResultingMemoryId = target.Id,
            MatchedMemoryId = target.Id,
        };
    }

    private async Task<MemoryDecision> SupersedeSemanticAsync(
        string userId, MemoryCandidate c, float[] embedding, List<MemoryEvidence> evidence,
        SemanticMemory old, string because, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var confidence = ConfidenceCalculator.Compute(c.ProposedConfidence, fromDirectUserStatement: true, corroborations: 0);

        var replacement = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = c.Subject ?? "user",
            Predicate = c.Predicate ?? "fact",
            Value = c.Value ?? c.Content,
            NormalizedFact = c.Content,
            Confidence = confidence,
            Importance = c.Importance,
            Validity = c.Validity,
            Status = MemoryStatus.Active,
            FirstObserved = now,
            LastConfirmed = now,
            CreatedAt = now,
            RelatedProject = c.RelatedProject,
            Embedding = embedding,
        };
        AttachEvidence(replacement.Evidence, evidence, replacement.Id, MemoryKind.Semantic, userId);

        await _curator.SupersedeSemanticAsync(
            userId, old.Id, replacement,
            $"Replaced \"{old.Value}\" with \"{replacement.Value}\" — {because}.", ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Superseded,
            Reason = $"supersedes the prior value \"{old.Value}\" (kept as history)",
            FinalConfidence = confidence,
            ResultingMemoryId = replacement.Id,
            MatchedMemoryId = old.Id,
        };
    }

    private async Task<MemoryDecision> NeedsReviewSemanticAsync(
        string userId, MemoryCandidate c, float[] embedding, List<MemoryEvidence> evidence,
        SemanticMemory conflicting, bool fromUser, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var confidence = ConfidenceCalculator.Compute(c.ProposedConfidence, fromUser, corroborations: 0);

        // Stored as a Candidate so it is NOT retrieved until reviewed/resolved (Phase 5).
        var memory = new SemanticMemory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = c.Subject ?? "user",
            Predicate = c.Predicate ?? "fact",
            Value = c.Value ?? c.Content,
            NormalizedFact = c.Content,
            Confidence = confidence,
            Importance = c.Importance,
            Validity = c.Validity,
            Status = MemoryStatus.Candidate,
            FirstObserved = now,
            LastConfirmed = now,
            CreatedAt = now,
            RelatedProject = c.RelatedProject,
            Embedding = embedding,
        };
        AttachEvidence(memory.Evidence, evidence, memory.Id, MemoryKind.Semantic, userId);
        await _store.AddSemanticAsync(memory, ct);
        await WriteRevisionAsync(userId, memory.Id, MemoryKind.Semantic, RevisionKind.Created,
            $"Held for review: contradicts existing value \"{conflicting.Value}\".",
            before: conflicting.NormalizedFact, after: memory.NormalizedFact, ct: ct);

        return new MemoryDecision
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.NeedsReview,
            Reason = $"contradicts existing fact \"{conflicting.Value}\" — held as candidate for review",
            FinalConfidence = confidence,
            ResultingMemoryId = memory.Id,
            MatchedMemoryId = conflicting.Id,
        };
    }

    // ---- helpers ----

    /// <summary>True if the candidate's own text or any of its cited excerpts contains a credential.</summary>
    private static bool LooksLikeSecret(MemoryCandidate c)
        => SecretDetector.LooksLikeSecret(c.Content)
            || SecretDetector.LooksLikeSecret(c.Value)
            || c.Evidence.Any(e => SecretDetector.LooksLikeSecret(e.Excerpt));

    private static List<MemoryCandidate> DedupeBatch(List<MemoryCandidate> items)
    {
        var byKey = new Dictionary<string, MemoryCandidate>();
        foreach (var c in items)
        {
            var key = c.Kind == MemoryKind.Semantic
                ? MemoryNormalizer.SemanticValueKey(c.Subject, c.Predicate, c.Value)
                : MemoryNormalizer.EpisodicKey(c.Content);

            if (byKey.TryGetValue(key, out var kept))
            {
                var merged = kept.Evidence
                    .Concat(c.Evidence)
                    .DistinctBy(e => (e.MessageId, e.Excerpt))
                    .ToList();
                byKey[key] = kept with { Evidence = merged };
            }
            else
            {
                byKey[key] = c;
            }
        }
        return byKey.Values.ToList();
    }

    private static (T? memory, double similarity) BestMatch<T>(IReadOnlyList<T> candidates, float[] embedding)
        where T : class, IMemory
    {
        T? best = null;
        var bestSim = 0.0;
        foreach (var m in candidates)
        {
            var sim = ScoreMath.Cosine(embedding, m.Embedding);
            if (sim > bestSim)
            {
                bestSim = sim;
                best = m;
            }
        }
        return (best, bestSim);
    }

    /// <summary>
    /// Keeps only the evidence whose excerpt sits in a sentence the user actually asserted. An
    /// excerpt from a message we can't read is kept — the guard refuses what it can prove is a
    /// non-assertion, not everything it can't confirm.
    /// </summary>
    private static List<MemoryEvidence> AssertedEvidence(
        List<MemoryEvidence> evidence, IReadOnlyDictionary<Guid, string> messageText)
        => evidence.Where(e => AssertionGuard.IsAsserted(e.Excerpt, Text(messageText, e))).ToList();

    private static string? Text(IReadOnlyDictionary<Guid, string> messageText, MemoryEvidence e)
        => messageText.TryGetValue(e.MessageId, out var text) ? text : null;

    private static List<MemoryEvidence> ValidEvidence(MemoryCandidate c, HashSet<Guid> validMessageIds)
        => c.Evidence
            .Where(e => validMessageIds.Contains(e.MessageId) && !string.IsNullOrWhiteSpace(e.Excerpt))
            .Select(e => new MemoryEvidence { MessageId = e.MessageId, Excerpt = e.Excerpt, Weight = 1.0 })
            .ToList();

    private static void AttachEvidence(
        ICollection<MemoryEvidence> target, List<MemoryEvidence> evidence,
        Guid memoryId, MemoryKind kind, string userId)
    {
        foreach (var e in evidence)
        {
            e.Id = Guid.NewGuid();
            e.UserId = userId;
            e.MemoryId = memoryId;
            e.MemoryKind = kind;
            target.Add(e);
        }
    }

    private async Task AddMergeEvidenceAsync(
        string userId, List<MemoryEvidence> evidence, Guid memoryId, MemoryKind kind, CancellationToken ct)
    {
        foreach (var e in evidence)
        {
            e.Id = Guid.NewGuid();
            e.UserId = userId;
            e.MemoryId = memoryId;
            e.MemoryKind = kind;
        }
        await _store.AddEvidenceAsync(userId, evidence, ct);
    }

    private Task WriteRevisionAsync(
        string userId, Guid memoryId, MemoryKind kind, RevisionKind revision, string note,
        string? before = null, string? after = null, CancellationToken ct = default)
        => _store.AddRevisionAsync(userId, new MemoryRevision
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MemoryId = memoryId,
            MemoryKind = kind,
            Kind = revision,
            Timestamp = _clock.GetUtcNow(),
            Actor = "extraction-pipeline",
            Note = note,
            Before = before,
            After = after,
        }, ct);

    private static MemoryDecision Reject(MemoryCandidate c, string reason, double confidence = 0)
        => new()
        {
            Candidate = c,
            Outcome = MemoryDecisionKind.Rejected,
            Reason = reason,
            FinalConfidence = confidence,
        };
}
