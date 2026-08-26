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
    private readonly ICognitiveCapture _capture;

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
        INliModel? nli = null,
        ICognitiveCapture? capture = null)
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
        _capture = capture ?? new NoCognitiveCapture();
    }

    public async Task<MemoryExtractionResult> ProcessAsync(
        string userId, IReadOnlyList<Message> exchange,
        ReferenceResolution? resolution = null, CancellationToken ct = default)
    {
        // 1. Generate. A consumable resolution is handed to the extractor so candidates state
        // the resolved meaning ("dinner for Beth") instead of the unresolved surface ("dinner
        // for her"). A guess is never shown to the extractor — it acts only as the veto
        // signal below.
        var consumable = resolution is { Consumable: true };
        var raw = await _extractor.ExtractAsync(userId, exchange, consumable ? resolution : null, ct);
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

        // The message that INTRODUCED a resolved referent is citable evidence too: a fact
        // stored as "dinner for Beth" from the words "dinner for her" should point at both the
        // current utterance and the one that named Beth. Registered here so the evidence
        // validation below accepts the extra citation attached per-candidate further down.
        if (consumable && resolution is { SourceMessageId: { } srcId, SourceExcerpt: { } srcText }
            && !validMessageIds.Contains(srcId))
        {
            validMessageIds.Add(srcId);
            messageText[srcId] = srcText;
        }

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
        foreach (var proposed in batch)
        {
            var candidate = proposed;

            // A pronoun stored as if it were a person's name is garbage with a database row —
            // the live specimen was "planning a small dinner for someone named her". When no
            // consumable resolution exists, the fact is unknowable, not misspellable; rejected
            // rather than "cleaned up", because there is nothing true to clean it up into.
            if (UnresolvedReferentGuard.IsPronounAsPerson(candidate))
            {
                _logger.LogInformation(
                    "Rejected a candidate memory for {UserId}: treats an unresolved pronoun as a person.", userId);
                decisions.Add(Reject(candidate, UnresolvedReferentGuard.Explanation));
                continue;
            }

            // An AMBIGUOUS reference this turn means any new person-name in a candidate is
            // somebody's invention — the user said a pronoun the system could not pin, so a
            // name here came from the model (in the first live run, from the chat model's own
            // guess in its reply, which the extractor then cited against the user's pronoun
            // sentence). Refused: the person the fact is about is exactly the thing nobody
            // knows.
            if (!consumable && resolution is not null
                && UnresolvedReferentGuard.NamesSomeoneTheUserDidNot(candidate, userSaid) is { } inventedName)
            {
                _logger.LogInformation(
                    "Rejected a candidate memory for {UserId}: names \"{Name}\" while the user's reference is ambiguous.",
                    userId, inventedName);
                decisions.Add(Reject(candidate,
                    $"names \"{inventedName}\" — the user said an ambiguous pronoun and never named them this turn"));
                continue;
            }

            // When the candidate states the resolved referent, attach the naming utterance as
            // additional evidence — provenance for BOTH what was said now and where the name
            // came from.
            if (consumable && resolution is { SourceMessageId: { } sourceId, SourceExcerpt: not null }
                && ReferencesResolvedName(candidate, resolution.Referent)
                && candidate.Evidence.All(e => e.MessageId != sourceId))
            {
                candidate = candidate with
                {
                    Evidence = candidate.Evidence
                        .Append(new CandidateEvidence(sourceId, resolution.SourceExcerpt))
                        .ToList(),
                };
            }

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

            // Somebody else's fact is not the user's, whatever the extractor labelled it. Sits with
            // the other vetoes rather than inside the semantic path because it is the same kind of
            // rule: a thing the store is not allowed to learn about this person.
            //
            // Refused rather than re-attributed. Knowing "Immy likes rockpooling" would be worth
            // having, but deriving whose it is from the sentence is guesswork, and the choice here
            // is between losing a fact about a daughter and inventing one about her father. Only
            // one of those is a lie.
            if (candidate.Kind == MemoryKind.Semantic
                && SubjectGuard.IsAboutSomeoneElse(candidate.Subject, candidate.Content))
            {
                _logger.LogInformation(
                    "Rejected a candidate memory for {UserId}: {Reason}.",
                    userId, SubjectGuard.Explain(candidate.Subject, candidate.Content));
                decisions.Add(Reject(candidate, SubjectGuard.Explain(candidate.Subject, candidate.Content)));
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

        // The same fact restated, word for word in the slot's own terms → confirmation.
        if (exact is not null)
        {
            await CapturePairAsync(candidate, exact, sameSlot: true, similarity: 1.0, "duplicate", ct);
            return await ConfirmSemanticAsync(exact, candidate, evidence, fromUser, ct);
        }

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

        // Why supersession did or didn't happen, at the moment it was decided. Worth keeping: every
        // supersession bug so far has been invisible from the outside, because the store simply
        // holds two facts and neither of them looks wrong on its own.
        _logger.LogDebug(
            "Supersession check for {Subject}/{Predicate}: slotMatches={Slots} slotSim={SlotSim:F3} " +
            "bar={Bar:F3} singleValued={Single} fromUser={FromUser} nearestSim={NearestSim:F3}",
            candidate.Subject, candidate.Predicate, slotMatches.Count, slotSim, singleValuedBar,
            FactSupersession.IsSingleValued(candidate.Predicate), fromUser, similarity);

        if (slotBest is not null
            && slotSim >= singleValuedBar
            && FactSupersession.IsSingleValued(candidate.Predicate))
        {
            await CapturePairAsync(candidate, slotBest, sameSlot: true, slotSim,
                fromUser ? "supersedes:single_valued" : "needs_review:single_valued", ct);
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

        // A multi-valued slot holds several true things at once, so replacing one of them needs to
        // say WHICH — and the wording signal is read from the whole turn, so on its own it never
        // can. In the `health` slot "I don't run any more, my knee's gone" is a real replacement,
        // and it retired a penicillin allergy standing next to it.
        //
        // What separates the two is not how similar they are — measured, that conflates "both
        // medical" with "the same fact" — but whether the new fact MENTIONS the old one. Someone
        // changing something names what they are changing:
        //
        //   "prefers oat milk lattes over BLACK COFFEE"   replacing  "black coffee without sugar"
        //   "no longer runs due to a knee issue"          replacing  "allergic to penicillin"   ✗
        //
        // The user's own words count too, because the naming is often there rather than in the
        // extracted sentence: "actually I've gone off TEA, coffee now" yields a fact about coffee
        // that would otherwise overlap nothing.
        //
        // Single-valued slots skip this: there is only ever one value they could mean.
        var namesWhatItReplaces =
            slotBest is null
            || FactSupersession.IsSingleValued(candidate.Predicate)
            || Mentions(candidate, slotBest);

        if (wordingSaysReplace && namesWhatItReplaces)
        {
            // Same slot only. The replacement signal is read from the whole turn, so it says
            // "something here is being changed" and NOT which thing — and letting it fall back to
            // the nearest memory of any kind is how "I don't run any more, my knee's given out"
            // retired a penicillin allergy. The audit trail recorded that as "the user said this
            // replaces it", which he had not: both memories were medical, they cleared the
            // similarity floor, and the wrong one was the closest.
            //
            // The cross-slot fallback existed because the extractor invented a predicate per
            // phrasing, so a changed fact rarely landed back where the old one was. The closed
            // vocabulary fixed that at the source, which leaves this doing nothing but damage.
            // Failing to supersede keeps two facts and looks untidy; superseding the wrong one
            // silently destroys something the user told us, and only one of those is recoverable.
            var replaced = slotBest;
            var replacedSim = slotSim;

            if (replaced is not null && replacedSim >= _options.ReplacementSimilarityThreshold)
            {
                await CapturePairAsync(candidate, replaced, sameSlot: true, replacedSim,
                    fromUser ? "supersedes:wording" : "needs_review:wording", ct);
                return fromUser
                    ? await SupersedeSemanticAsync(
                        userId, candidate, embedding, evidence, replaced, "the user said this replaces it", ct)
                    : await NeedsReviewSemanticAsync(userId, candidate, embedding, evidence, replaced, fromUser, ct);
            }
        }

        // Only now: a paraphrase of something already held. This used to run BEFORE the slot rules
        // and swallowed corrections whole. "Scott" → "Scott Donald" and "allergic to penicillin" →
        // "allergic to amoxicillin" are textually near-identical to what they correct, so they
        // cleared the duplicate threshold and were recorded as CONFIRMATIONS of the very facts they
        // were fixing — saying "I was wrong" raised her confidence in the wrong answer.
        //
        // Nothing is lost by the move. A genuine restatement has the same value and is caught by
        // the exact key above; what reaches here always differs in value, and a different value is
        // not a restatement however similar the sentence reads. Cardinality and the replacement
        // rules get first refusal, and this catches what they decline.
        if (nearest is not null && similarity >= _options.DuplicateSimilarityThreshold)
        {
            await CapturePairAsync(
                candidate, nearest, IsSameSlot(candidate, nearest), similarity, "duplicate:similar", ct);
            return await ConfirmSemanticAsync(nearest, candidate, evidence, fromUser, ct);
        }

        // 5. Otherwise a new fact. If it landed beside something — same slot, or near enough that
        // the replacement rules were even in play — the decision NOT to supersede is a decision
        // too, and the pair corpus needs those every bit as much as the replacements: a model
        // trained only on the pairs that superseded learns that everything supersedes.
        var beside = slotBest ?? (similarity >= _options.ReplacementSimilarityThreshold ? nearest : null);
        if (beside is not null)
        {
            var besideSim = ReferenceEquals(beside, slotBest) ? slotSim : similarity;
            await CapturePairAsync(
                candidate, beside, ReferenceEquals(beside, slotBest), besideSim, "coexist", ct);
        }

        // Score confidence and accept if it clears the bar.
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
    /// <summary>
    /// Whether the incoming fact, or the words the user used for it, actually refer to the memory
    /// it would replace. Token overlap rather than embedding similarity on purpose: the question is
    /// "are these about the same thing", and two unrelated medical facts are very similar while
    /// sharing no subject matter at all.
    /// </summary>
    private static bool Mentions(MemoryCandidate candidate, SemanticMemory old)
    {
        // Values and the user's own words, never the normalized fact. Every normalized fact starts
        // "The user…", so comparing those guarantees a shared token and an overlap that is never
        // zero — which is exactly how this guard silently did nothing on its first attempt.
        var said = string.Join(
            " ", new[] { candidate.Value }.Concat(candidate.Evidence.Select(e => e.Excerpt)));
        return ScoreMath.KeywordOverlap(said, old.Value) > 0;
    }

    /// <summary>
    /// One pair, one row, at the moment the decision was made. Never throws into the turn and
    /// never runs a model — see ICognitiveCapture. Ages are computed here because only the
    /// pipeline holds both the memory and the clock at decision time.
    /// </summary>
    private async Task CapturePairAsync(
        MemoryCandidate candidate, SemanticMemory existing, bool sameSlot, double similarity,
        string outcome, CancellationToken ct)
    {
        if (!_capture.IsCapturing)
            return;

        var now = _clock.GetUtcNow();
        await _capture.CapturePairAsync(new SupersessionPairCapture(
            IncomingFact: candidate.Content,
            IncomingValue: candidate.Value,
            Predicate: candidate.Predicate,
            Utterance: string.Join(" ", candidate.Evidence.Select(e => e.Excerpt)),
            ExistingId: existing.Id,
            ExistingFact: existing.NormalizedFact,
            ExistingValue: existing.Value,
            ExistingPredicate: existing.Predicate,
            ExistingAgeDays: (int)Math.Max(0, (now - existing.FirstObserved).TotalDays),
            ExistingConfirmedDays: (int)Math.Max(0, (now - existing.LastConfirmed).TotalDays),
            SameSlot: sameSlot,
            SingleValued: FactSupersession.IsSingleValued(candidate.Predicate),
            Similarity: similarity,
            IncumbentOutcome: outcome,
            UserId: existing.UserId), ct);
    }

    private static bool ReferencesResolvedName(MemoryCandidate candidate, string referent)
        => candidate.Content.Contains(referent, StringComparison.OrdinalIgnoreCase)
           || (candidate.Value?.Contains(referent, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsSameSlot(MemoryCandidate candidate, SemanticMemory memory)
        => MemoryNormalizer.SemanticSlotKey(candidate.Subject, candidate.Predicate)
            == MemoryNormalizer.SemanticSlotKey(memory.Subject, memory.Predicate);

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
