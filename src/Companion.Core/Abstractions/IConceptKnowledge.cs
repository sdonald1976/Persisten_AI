using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.Core.Abstractions;

/// <summary>
/// Ava's concept-knowledge faculty (docs/CONCEPT_KNOWLEDGE.md): learn from explicit
/// teaching in a USER message; answer "does she know X?" from the store. The epistemic
/// boundary lives behind this interface — nothing else may decide what Ava knows.
/// </summary>
public interface IConceptKnowledge
{
    /// <summary>Learns from an explicit definitional teaching in the message, if one is
    /// present and passes every gate. Returns the taught concept's display name, or null.
    /// Non-user messages NEVER teach — that is the laundering barrier, not an oversight.</summary>
    Task<string?> LearnFromAsync(
        string userId, Message message, PersonaLexicon? lexicon = null, CancellationToken ct = default);

    /// <summary>Ava's epistemic state toward a term, by exact concept/alias lookup.</summary>
    Task<ConceptLookupResult> LookupAsync(string userId, string term, CancellationToken ct = default);
}
