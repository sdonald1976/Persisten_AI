using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Options;

namespace Companion.Core.Turns.Context;

/// <summary>What retrieval found, before anything is written about it.</summary>
public sealed record TurnRetrievalResult
{
    /// <summary>The retriever's own outcome: selected, excluded-with-reasons, scores, query embedding.</summary>
    public required RetrievalOutcome Outcome { get; init; }

    /// <summary>Associative expansion, appended after the direct hits and never re-ranked.</summary>
    public required IReadOnlyList<RetrievalResult> Associative { get; init; }

    /// <summary>What the packet will actually carry: direct hits then associative, in that order.</summary>
    public required IReadOnlyList<RetrievalResult> Selected { get; init; }
}

/// <summary>
/// The contextual ingredients the packet consumes, as typed values rather than prose.
///
/// Deliberately NOT combined into text here. Assembly decides ordering, labelling and
/// budgeting, and doing any of that early would move a decision out of the component that
/// owns it.
/// </summary>
public sealed record TurnContextResult
{
    public required RelationshipSnapshot Relationship { get; init; }

    /// <summary>A past thought that colors this turn, already aged into words. Null if none fits.</summary>
    public string? Musing { get; init; }

    /// <summary>At most one held curiosity, offered for the model to raise if it fits.</summary>
    public Curiosity? Curiosity { get; init; }

    public required CompanionStateSnapshot InnerState { get; init; }
    public required FamiliaritySnapshot Familiarity { get; init; }

    public required IReadOnlyList<string> PreferenceNotes { get; init; }
    public required IReadOnlyList<string> AttentionNotes { get; init; }
    public required IReadOnlyList<string> ProcedureNotes { get; init; }
    public string? CapabilityNote { get; init; }
    public required IReadOnlyList<string> PerspectiveNotes { get; init; }

    /// <summary>
    /// Decisions produced here, in order. Returned rather than written so the caller appends
    /// them at exactly the point the turn always did.
    /// </summary>
    public required IReadOnlyList<DecisionRecord> Decisions { get; init; }
}

/// <summary>
/// The third stage of a turn: gather what is factually true and relevant, before anything is
/// planned or said.
///
/// It is several narrowly named methods rather than one, because the turn's existing data
/// dependencies genuinely interleave with other stages and hiding that would mean reordering
/// the turn:
///
///   <list type="number">
///   <item><see cref="LoadHistoryAsync"/> runs BEFORE understanding, because the
///   working-context read is over the recent transcript.</item>
///   <item><see cref="RetrieveAsync"/> runs after understanding, because the retrieval query
///   is what understanding produced.</item>
///   <item><see cref="LookupKnowledgeAsync"/> runs next, answering "do you know what X is?"
///   from the concept store rather than from the model's pretraining.</item>
///   <item><see cref="PrepareAsync"/> runs last, after intent completion and after this
///   turn's mood has been captured — her own state is read AFTER the signal has rubbed off.</item>
///   </list>
///
/// It owns nothing beyond gathering. Not planning, not packet assembly, not compaction, not
/// tools, not model calls, not renderer selection, not persistence, not shadow recording. The
/// promotions that inject an interpretation note are decisions ABOUT the context rather than
/// part of gathering it, and they stayed with the turn.
/// </summary>
public sealed class TurnContext(
    IRetriever retriever,
    IAssociativeRecallService associativeRecall,
    IRelationshipTracker relationship,
    IFamiliarityTracker familiarity,
    ICapabilityRegistry capabilities,
    ISharedPerspectiveStore sharedPerspectives,
    IPreferenceStore preferences,
    IConversationStore conversations,
    IReflectionStore reflections,
    ICompanionStateTracker innerState,
    IAttentionService attention,
    IProcedureStore procedures,
    IConceptKnowledge? concepts,
    IOptions<CompanionOptions> options)
{
    private readonly CompanionOptions _options = options.Value;

    // Unchanged from where they lived before: same lookback, same floors, same window.
    private static readonly TimeSpan MusingSurfaceWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan MusingIsRecent = TimeSpan.FromHours(36);
    private const int MusingSearchLookback = 50;
    private const double MusingRelevanceFloor = 0.3;
    private const double PreferenceRelevanceFloor = 0.25;
    private const int MaxPreferenceNotes = 2;

    /// <summary>
    /// Recent prior turns, excluding the message being handled. Fetched before retrieval
    /// because the working-context read shapes the retrieval query.
    /// </summary>
    public async Task<IReadOnlyList<Message>> LoadHistoryAsync(
        Guid conversationId, string userId, Guid excludeMessageId, CancellationToken ct = default)
        => (await conversations.GetRecentMessagesAsync(
                conversationId, userId, _options.RecentMessageCount + 1, ct))
            .Where(m => m.Id != excludeMessageId)
            .ToList();

    /// <summary>
    /// Retrieves what the message MEANS — question and answer, reference and referent —
    /// rather than what it says, then expands associatively.
    /// </summary>
    public async Task<TurnRetrievalResult> RetrieveAsync(
        string userId, string retrievalQuery, string? resolvedProjectName,
        CancellationToken ct = default)
    {
        var outcome = await retriever.RetrieveAsync(userId, retrievalQuery, resolvedProjectName, ct);
        var associative = await associativeRecall.ExpandAsync(
            userId, retrievalQuery, outcome.Selected, _options.MaxAssociativeMemories, ct);

        return new TurnRetrievalResult
        {
            Outcome = outcome,
            Associative = associative,
            Selected = outcome.Selected.Concat(associative).ToList(),
        };
    }

    /// <summary>
    /// The before/after evidence for whether reference resolution changes what reaches the
    /// prompt: the same turn retrieved with the RAW message. Costs one extra embedding, on
    /// rewritten turns only, and only while measuring.
    /// </summary>
    public async Task<IReadOnlyList<string>> RetrieveWithRawQueryAsync(
        string userId, string rawQuery, string? resolvedProjectName, CancellationToken ct = default)
    {
        var rawOutcome = await retriever.RetrieveAsync(userId, rawQuery, resolvedProjectName, ct);
        return rawOutcome.Selected.Take(5)
            .Select(r => (r.Memory.Content.Length <= 120 ? r.Memory.Content : r.Memory.Content[..120])
                + $" (score {r.Score:F2})").ToList();
    }

    /// <summary>
    /// Answers an epistemic question from the concept store. Returns null when the turn asks
    /// none, or when concept knowledge is not wired.
    /// </summary>
    public async Task<(ConceptLookupResult Result, string AskedTerm)?> LookupKnowledgeAsync(
        string userId, string promptText, CancellationToken ct = default)
    {
        if (concepts is null || KnowledgeQuestionDetector.Detect(promptText) is not { } askedTerm)
            return null;

        return (await concepts.LookupAsync(userId, askedTerm, ct), askedTerm);
    }

    /// <summary>
    /// Everything else the packet consumes. Runs after intent completion and after this
    /// turn's mood capture, so her own state reflects the signal this message just left.
    /// </summary>
    public async Task<TurnContextResult> PrepareAsync(
        string userId,
        string promptText,
        DateTimeOffset now,
        float[]? queryEmbedding,
        IReadOnlyList<RetrievalResult> selectedMemories,
        PromptIdentityContext identities,
        CancellationToken ct = default)
    {
        var decisions = new List<DecisionRecord>();

        var relationshipSnapshot = await relationship.BuildAsync(userId, ct);

        // Reading the diary is side-effect free: a musing accompanies many turns. A curiosity
        // is consumed the one time it is offered, which is why only one is taken.
        var musing = await RelevantMusingAsync(userId, queryEmbedding, now, ct);
        var curiosity = await reflections.GetNextToVoiceAsync(
            userId, now, TimeSpan.FromHours(_options.CuriosityCooldownHours), ct);
        decisions.Add(new DecisionRecord
        {
            Stage = "curiosity", Decider = "rule",
            Verdict = curiosity is null ? "none-offered" : "offered",
            Reason = curiosity?.Question,
        });

        var state = await innerState.BuildAsync(userId, ct);
        var familiaritySnapshot = await familiarity.BuildAsync(userId, ct);

        var preferenceNotes = await RelevantPreferencesAsync(
            userId, queryEmbedding, identities.CompanionRef, ct);
        var attentionNotes = await attention.SelectForContextAsync(
            userId, promptText, _options.MaxAttentionItems, ct);
        var procedureNotes = await RelevantProceduresAsync(
            userId, promptText, identities.UserRef, ct);
        var capabilityNote = await capabilities.RenderSummaryAsync(promptText, ct);
        var perspectiveNotes = await SharedPerspectiveNotesAsync(
            userId, selectedMemories, identities, ct);

        return new TurnContextResult
        {
            Relationship = relationshipSnapshot,
            Musing = musing,
            Curiosity = curiosity,
            InnerState = state,
            Familiarity = familiaritySnapshot,
            PreferenceNotes = preferenceNotes,
            AttentionNotes = attentionNotes,
            ProcedureNotes = procedureNotes,
            CapabilityNote = capabilityNote,
            PerspectiveNotes = perspectiveNotes,
            Decisions = decisions,
        };
    }

    // ---- moved verbatim from Companion ------------------------------------------------------

    /// <summary>
    /// The musing that should color THIS turn: the most relevant past thought by similarity
    /// to the turn's query — an old thought resurfaces on its own when the conversation comes
    /// back to it — falling back to the freshest one while it is still current.
    /// </summary>
    private async Task<string?> RelevantMusingAsync(
        string userId, float[]? queryEmbedding, DateTimeOffset now, CancellationToken ct)
    {
        var musings = (await reflections.GetRecentAsync(userId, MusingSearchLookback, ct))
            .Where(r => r.HasMusing)
            .ToList();
        if (musings.Count == 0)
            return null;

        if (queryEmbedding is not null)
        {
            var best = musings
                .Where(r => r.Embedding is not null)
                .Select(r => (Reflection: r, Score: ScoreMath.Cosine(queryEmbedding, r.Embedding!)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best.Reflection is not null && best.Score >= MusingRelevanceFloor)
                return WithAge(best.Reflection, now);
        }

        var newest = musings[0];
        return now - newest.CreatedAt <= MusingSurfaceWindow ? WithAge(newest, now) : null;
    }

    private static string WithAge(Reflection reflection, DateTimeOffset now)
        => now - reflection.CreatedAt <= MusingIsRecent
            ? reflection.Musing!
            : $"(a thought from {RelativeTime.Describe(now - reflection.CreatedAt)} ago) {reflection.Musing}";

    /// <summary>Her tastes that are actually relevant to what is being discussed.</summary>
    private async Task<IReadOnlyList<string>> RelevantPreferencesAsync(
        string userId, float[]? queryEmbedding, string companionName, CancellationToken ct)
    {
        if (queryEmbedding is null)
            return Array.Empty<string>();

        var all = await preferences.GetAllAsync(userId, ct);
        return all
            .Where(p => p.Embedding is not null)
            .Select(p => (Preference: p, Score: ScoreMath.Cosine(queryEmbedding, p.Embedding!)))
            .Where(x => x.Score >= PreferenceRelevanceFloor)
            .OrderByDescending(x => x.Score)
            .Take(MaxPreferenceNotes)
            .Select(x => x.Preference.Describe(companionName))
            .ToList();
    }

    private async Task<IReadOnlyList<string>> RelevantProceduresAsync(
        string userId, string query, string userName, CancellationToken ct)
    {
        var found = await procedures.SearchAsync(userId, query, _options.MaxProceduresInContext, ct);
        return found.Select(p =>
        {
            var activeSteps = p.Steps.Where(s => s.IsActive).OrderBy(s => s.Order).Take(8).ToList();
            var steps = string.Join(" ", activeSteps.Select(s => $"{s.Order}. {s.Instruction}"));
            return $"{userName}'s {p.Name} ({p.Access}; authoritative user-taught workflow): {steps}";
        }).ToList();
    }

    private async Task<IReadOnlyList<string>> SharedPerspectiveNotesAsync(
        string userId, IReadOnlyList<RetrievalResult> selected, PromptIdentityContext identities,
        CancellationToken ct)
    {
        var sharedIds = selected
            .Select(r => r.Memory)
            .OfType<EpisodicMemory>()
            .Where(m => m.Owner == MemoryOwner.Shared)
            .Select(m => m.Id)
            .ToList();
        var perspectives = await sharedPerspectives.GetForExperiencesAsync(userId, sharedIds, ct);
        return perspectives
            .Take(_options.MaxSharedPerspectivesInContext)
            .Select(p =>
            {
                var owner = p.Owner == MemoryOwner.Companion ? identities.CompanionRef : identities.UserRef;
                return $"{owner} perspective: {p.Summary} (interpretation; confidence {Math.Round(p.Confidence, 2)})";
            })
            .ToList();
    }
}
