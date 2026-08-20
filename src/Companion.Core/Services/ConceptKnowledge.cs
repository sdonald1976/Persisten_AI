using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Core.Services;

/// <summary>
/// Ava's concept-knowledge faculty: learning from explicit teaching, and answering "does
/// she know X?" from her store. The epistemic ownership boundary is enforced here
/// structurally, not by prompt hope: assertions form only from USER-authored messages (the
/// chat model's own words are uncitable, closing the Epcot laundering path at the store
/// boundary), only from high-precision teaching shapes, and only with evidence + origin —
/// there is no code path by which pretrained model knowledge becomes a row.
/// </summary>
public sealed class ConceptKnowledge : IConceptKnowledge
{
    private readonly IConceptStore _store;
    private readonly IEmbeddingModel _embeddings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConceptKnowledge> _logger;

    public ConceptKnowledge(
        IConceptStore store, IEmbeddingModel embeddings, TimeProvider clock,
        ILogger<ConceptKnowledge> logger)
    {
        _store = store;
        _embeddings = embeddings;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string?> LearnFromAsync(
        string userId, Message message, PersonaLexicon? lexicon = null, CancellationToken ct = default)
    {
        // The structural laundering barrier: only the user teaches. Whatever the model's
        // reply explains, it cannot become Ava's knowledge — not this turn, not ever.
        if (message.Role != MessageRole.User)
            return null;

        var teaching = TeachingDetector.Detect(message.Content);
        if (teaching is null)
            return null;
        if (SecretDetector.LooksLikeSecret(teaching.Sentence))
            return null;
        if (lexicon is not null
            && (lexicon.MentionsCompanion(teaching.Term) || lexicon.MentionsCompanion(teaching.Gloss)))
            return null; // fiction about the character is not world knowledge

        var now = _clock.GetUtcNow();
        var canonical = Canonical(teaching.Term);
        var concept = await _store.FindByNameAsync(userId, canonical, ct);
        if (concept is null)
        {
            concept = new Concept
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CanonicalName = canonical,
                DisplayName = teaching.Term,
                Kind = ConceptKind.Other,
                CreatedAt = now,
            };
            await _store.AddConceptAsync(concept, ct);
        }

        var confidence = ConfidenceCalculator.Compute(0.8, fromDirectUserStatement: true, corroborations: 0);
        var existing = (await _store.GetAssertionsAsync(userId, concept.Id, ct))
            .FirstOrDefault(a => a.Relation == ConceptRelation.DefinedAs
                && a.Status == MemoryStatus.Active && a.Validity == Validity.Current);

        // Re-taught verbatim: a confirmation, not a new fact.
        if (existing is not null
            && string.Equals(existing.NormalizedText, teaching.Sentence, StringComparison.OrdinalIgnoreCase))
        {
            await _store.ConfirmAssertionAsync(existing, confidence, now, ct);
            return concept.DisplayName;
        }

        var assertion = new ConceptAssertion
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConceptId = concept.Id,
            Relation = ConceptRelation.DefinedAs,
            Value = teaching.Gloss,
            NormalizedText = teaching.Sentence,
            Origin = KnowledgeOrigin.Taught,
            Confidence = confidence,
            FirstObserved = now,
            LastConfirmed = now,
            CreatedAt = now,
            Embedding = await _embeddings.EmbedAsync(teaching.Sentence, ct),
        };
        assertion.Evidence.Add(new MemoryEvidence
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MemoryId = assertion.Id,
            MemoryKind = MemoryKind.Concept,
            MessageId = message.Id,
            Excerpt = teaching.Sentence,
            Weight = 1.0,
        });

        // DefinedAs is single-valued: a different definition replaces, with history.
        if (existing is not null)
            await _store.SupersedeAssertionAsync(existing, assertion, ct);
        else
            await _store.AddAssertionAsync(assertion, ct);

        _logger.LogInformation("Learned a concept for {UserId}: \"{Term}\".", userId, concept.DisplayName);
        return concept.DisplayName;
    }

    public async Task<ConceptLookupResult> LookupAsync(
        string userId, string term, CancellationToken ct = default)
    {
        var canonical = Canonical(term);
        var concept = await _store.FindByNameAsync(userId, canonical, ct)
            ?? (canonical.EndsWith('s')
                ? await _store.FindByNameAsync(userId, canonical.TrimEnd('s'), ct)
                : null);
        if (concept is null)
            return new ConceptLookupResult(ConceptFamiliarity.Unknown, term);

        var assertions = await _store.GetAssertionsAsync(userId, concept.Id, ct);
        var defining = assertions.FirstOrDefault(a => a.Relation == ConceptRelation.DefinedAs
            && a.Validity == Validity.Current && a.Status != MemoryStatus.Deleted);

        if (defining is { Status: MemoryStatus.Disputed })
            return new ConceptLookupResult(ConceptFamiliarity.Disputed, concept.DisplayName,
                defining.NormalizedText, defining.FirstObserved, defining.Origin);

        var active = assertions.Where(a => a.Status == MemoryStatus.Active
            && a.Validity == Validity.Current).ToList();
        if (active.Count > 0)
        {
            var best = active.FirstOrDefault(a => a.Relation == ConceptRelation.DefinedAs) ?? active[0];
            return new ConceptLookupResult(ConceptFamiliarity.Known, concept.DisplayName,
                best.NormalizedText, best.FirstObserved, best.Origin);
        }

        return new ConceptLookupResult(
            assertions.Any(a => a.Status == MemoryStatus.Candidate)
                ? ConceptFamiliarity.Learning : ConceptFamiliarity.Heard,
            concept.DisplayName);
    }

    /// <summary>Trimmed, lower-cased, article-stripped — the exact-match key.</summary>
    public static string Canonical(string term)
    {
        var t = term.Trim().Trim('"', '\'', '.', '?', '!').ToLowerInvariant();
        foreach (var article in new[] { "a ", "an ", "the " })
            if (t.StartsWith(article, StringComparison.Ordinal))
                return t[article.Length..].Trim();
        return t;
    }
}

/// <summary>
/// The direct epistemic question, detected deterministically: "do you know what an axe
/// is?", "what do you know about gravity?", "have I taught you about tides?". High
/// precision like every gate on this boundary — an ordinary question about the world is
/// NOT a knowledge question and must not trigger an authoritative not-learned note.
/// </summary>
public static partial class KnowledgeQuestionDetector
{
    /// <summary>The asked-about term, or null when this isn't a knowledge question.</summary>
    public static string? Detect(string message)
    {
        var m = KnowledgeQuestion().Match(message.Trim());
        if (!m.Success)
            return null;
        var term = m.Groups["term"].Value.Trim().Trim('?', '.', '!', '"', '\'');
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A term containing a pronoun ("her favorite axe") is a conversational reference,
        // not an epistemic probe — working context's jurisdiction, not this detector's.
        if (words.Length is 0 or > 3 || words.Any(w => Pronoun().IsMatch(w)))
            return null;
        return term;
    }

    [GeneratedRegex(
        @"^(do you know what (?:(?:a|an|the)\s+)?(?<term>[\w' -]+?)\s+(is|are|means)|" +
        @"do you know about (?:(?:a|an|the)\s+)?(?<term>[\w' -]+?)|" +
        @"what do you know about (?:(?:a|an|the)\s+)?(?<term>[\w' -]+?)|" +
        @"have i taught you (about )?(?:(?:a|an|the)\s+)?(?<term>[\w' -]+?))\s*[?.!]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KnowledgeQuestion();

    [GeneratedRegex(@"^(my|your|his|her|its|our|their|this|that|it|he|she|they)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pronoun();
}
