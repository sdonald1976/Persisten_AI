using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Activities;

/// <summary>
/// Explicit activation resolution (Source 1b §6). Activation requires an UNAMBIGUOUS
/// resolved procedure definition from the real store: text search may produce candidates,
/// but ambiguity yields a diagnosed non-activation or a clarification requirement — never
/// a silent pick. Shadow activation alters no production state and no displayed behavior.
/// </summary>
public sealed class ActivityActivationResolver(IProcedureStore procedures)
{
    /// <summary>Phrases that count as an explicit request. Topic similarity does NOT.</summary>
    private static readonly string[] ExplicitTriggers =
        ["let's play", "lets play", "want to play", "shall we play", "play a game of", "play 20 questions",
         "play twenty questions", "start a game"];

    public sealed record ActivationDecision(
        bool Activated,
        ActivityDefinition? Definition,
        Guid? ProcedureId,
        string? Evidence,
        string? Reason,
        IReadOnlyList<string> Candidates)
    {
        public bool NeedsClarification => !Activated && Reason == "ambiguous-procedure";
    }

    /// <summary>
    /// Resolves an activation request. Returns Activated only when the message is an
    /// explicit request AND exactly one procedure definition matches.
    /// </summary>
    public async Task<ActivationDecision> ResolveAsync(
        string userId, string userMessage, Guid messageId,
        string activityType, string strategyVersion,
        string askerParticipantId, string answererParticipantId,
        int questionLimit, CancellationToken ct = default)
    {
        if (!IsExplicitRequest(userMessage))
            return new ActivationDecision(false, null, null, null, "not-an-explicit-request", []);

        IReadOnlyList<Procedure> found;
        try
        {
            found = await procedures.SearchAsync(userId, activityType.Replace('-', ' '), limit: 5, ct);
        }
        catch (Exception ex)
        {
            return new ActivationDecision(false, null, null, null,
                $"procedure-search-failed:{ex.GetType().Name}", []);
        }

        var matches = found
            .Where(p => p.Status == ProcedureStatus.Active)
            .Where(p => Normalize(p.Name).Contains(Normalize(activityType))
                        || Normalize(activityType).Contains(Normalize(p.Name)))
            .ToList();

        if (matches.Count == 0)
            return new ActivationDecision(false, null, null, null, "no-matching-procedure",
                found.Select(p => p.Name).ToList());

        if (matches.Count > 1)
            return new ActivationDecision(false, null, null, null, "ambiguous-procedure",
                matches.Select(p => p.Name).ToList());

        var procedure = matches[0];
        var definition = new ActivityDefinition(
            activityType, strategyVersion, procedure.Id,
            questionLimit, askerParticipantId, answererParticipantId);

        return new ActivationDecision(
            true, definition, procedure.Id,
            Evidence: $"message:{messageId} procedure:{procedure.Id}",
            Reason: null, Candidates: []);
    }

    public static bool IsExplicitRequest(string message)
        => ExplicitTriggers.Any(t => message.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hyphens and spaces are the same separator: "twenty-questions" matches
    /// a procedure taught as "twenty questions".</summary>
    private static string Normalize(string s)
        => new(s.ToLowerInvariant().Replace('-', ' ')
            .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
}
