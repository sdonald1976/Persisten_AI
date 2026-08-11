using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// Assembles a bounded, labeled context packet. Each memory is tagged with its provenance
/// (direct statement / inference / possibly outdated) and the memory section is capped by a
/// token budget so the prompt never balloons. Outdated items produce explicit uncertainty notes.
/// </summary>
public sealed class ContextAssembler : IContextAssembler
{
    private readonly CompanionOptions _options;

    public ContextAssembler(IOptions<CompanionOptions> options) => _options = options.Value;

    public ContextPacket Assemble(
        string userMessage,
        IReadOnlyList<Message> recentMessages,
        IReadOnlyList<RetrievalResult> retrieved,
        ProjectContext projectContext,
        string? persona = null,
        RelationshipSnapshot? relationship = null,
        string? musing = null,
        string? curiosity = null,
        string? companionMood = null,
        string? familiarity = null,
        string? temporal = null,
        IReadOnlyList<string>? preferences = null,
        PromptIdentityContext? identities = null)
    {
        var items = new List<ContextItem>();
        var notes = new List<string>();
        var budget = _options.MemoryTokenBudget;
        var usedTokens = 0;

        foreach (var result in retrieved)
        {
            var text = result.Memory.Content;
            if (ConflictsWithAuthoritativeIdentity(result.Memory, identities))
            {
                notes.Add("A retrieved identity memory conflicted with the authoritative profile identity and was withheld from current context.");
                continue;
            }

            var cost = EstimateTokens(text);
            if (usedTokens + cost > budget)
                continue; // respect the memory token budget; skip the rest

            var provenance = ClassifyProvenance(result.Memory);
            items.Add(new ContextItem
            {
                Text = text,
                Provenance = provenance,
                Note = provenance == ContextProvenance.Outdated ? "was true previously" : null,
                Owner = result.Memory.Owner,
            });
            usedTokens += cost;

            if (provenance == ContextProvenance.Outdated)
                notes.Add($"\"{Truncate(text)}\" may no longer be current.");
        }

        if (projectContext.Resolution.RequiresClarification &&
            projectContext.Resolution.ClarificationQuestion is { } question)
        {
            notes.Add($"The project reference is ambiguous — ask to clarify rather than guessing: {question}");
        }

        var recentCost = recentMessages.Sum(m => EstimateTokens(m.Content));

        return new ContextPacket
        {
            UserMessage = userMessage,
            Persona = persona,
            Identities = identities,
            RecentMessages = recentMessages,
            Memories = items,
            Project = projectContext.Summary,
            OpenLoops = projectContext.OpenLoops,
            ClarificationQuestion = projectContext.Resolution.ClarificationQuestion,
            RelationshipNote = relationship?.Describe(),
            Musing = musing,
            CuriosityQuestion = curiosity,
            MoodNote = companionMood,
            RegisterNote = RegisterAdvisor.Advise(userMessage),
            FamiliarityNote = familiarity,
            TemporalNote = temporal,
            PreferenceNotes = preferences ?? Array.Empty<string>(),
            UncertaintyNotes = notes,
            EstimatedTokens = usedTokens + recentCost + EstimateTokens(userMessage),
        };
    }

    private static ContextProvenance ClassifyProvenance(IMemory memory)
    {
        if (memory.Status == MemoryStatus.Superseded)
            return ContextProvenance.Outdated;

        if (memory is SemanticMemory sem)
        {
            if (sem.Validity is Validity.Historical or Validity.Superseded)
                return ContextProvenance.Outdated;

            // System-generated knowledge (inferred or consolidated) is never a direct quote.
            if (sem.Origin is MemoryOrigin.Inferred or MemoryOrigin.Consolidated)
                return ContextProvenance.Inferred;
        }

        // Lower-confidence memories are treated as inferences rather than direct statements.
        return memory.Confidence < 0.6 ? ContextProvenance.Inferred : ContextProvenance.DirectStatement;
    }

    private static bool ConflictsWithAuthoritativeIdentity(IMemory memory, PromptIdentityContext? identities)
    {
        if (identities is null || memory is not SemanticMemory sem)
            return false;
        if (sem.Validity is Validity.Historical or Validity.Superseded || sem.Status == MemoryStatus.Superseded)
            return false;

        var predicate = sem.Predicate ?? "";
        var content = sem.NormalizedFact ?? "";
        var value = sem.Value ?? "";
        if (!predicate.Contains("name", StringComparison.OrdinalIgnoreCase)
            && !content.Contains(" name ", StringComparison.OrdinalIgnoreCase)
            && !content.Contains("'s name", StringComparison.OrdinalIgnoreCase))
            return false;

        if (sem.Owner == MemoryOwner.User || sem.Subject.Equals("user", StringComparison.OrdinalIgnoreCase))
            return HasDifferentKnownValue(identities.UserName, value, content);

        if (sem.Owner == MemoryOwner.Companion
            || sem.Subject.Equals("companion", StringComparison.OrdinalIgnoreCase)
            || sem.Subject.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(identities.CompanionName)
                && sem.Subject.Equals(identities.CompanionName, StringComparison.OrdinalIgnoreCase)))
            return HasDifferentKnownValue(identities.CompanionName, value, content);

        return false;
    }

    private static bool HasDifferentKnownValue(string? authoritative, string value, string content)
    {
        if (string.IsNullOrWhiteSpace(authoritative))
            return false;
        var name = authoritative.Trim();
        if (value.Contains(name, StringComparison.OrdinalIgnoreCase)
            || content.Contains(name, StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(value)
            || content.Contains("name is", StringComparison.OrdinalIgnoreCase)
            || content.Contains("'s name", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rough token estimate (~4 chars/token). Good enough for budgeting.</summary>
    internal static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    private static string Truncate(string text, int max = 60)
        => text.Length <= max ? text : text[..max] + "…";
}
