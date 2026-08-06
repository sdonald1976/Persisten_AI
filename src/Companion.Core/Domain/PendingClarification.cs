using System.Text.Json;

namespace Companion.Core.Domain;

/// <summary>The kind of reference that was ambiguous.</summary>
public enum AmbiguityType
{
    Project,
}

/// <summary>Lifecycle of a pending clarification.</summary>
public enum ClarificationStatus
{
    Pending,
    Resolved,
    Cancelled,
    Expired,
}

/// <summary>
/// An unresolved ambiguity, persisted so a turn can pause for clarification and resume later —
/// even across an application restart. It carries everything needed to reconstruct the original
/// request once the user says which candidate they meant.
/// </summary>
public sealed class PendingClarification
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;
    public Guid ConversationId { get; set; }

    /// <summary>The user message whose reference was ambiguous (the turn to resume).</summary>
    public Guid OriginalMessageId { get; set; }
    public string OriginalText { get; set; } = default!;

    public AmbiguityType AmbiguityType { get; set; } = AmbiguityType.Project;

    /// <summary>Ranked candidate entities/projects, serialized (see <see cref="ClarificationCandidate"/>).</summary>
    public string CandidatesJson { get; set; } = "[]";

    /// <summary>The exact deterministic question that was asked.</summary>
    public string Question { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
    public ClarificationStatus Status { get; set; } = ClarificationStatus.Pending;

    // Resolution evidence / audit trail.
    public Guid? ResolvedProjectId { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public IReadOnlyList<ClarificationCandidate> Candidates() => ClarificationCandidate.Parse(CandidatesJson);
}

/// <summary>One ranked candidate offered in a clarification question.</summary>
public sealed record ClarificationCandidate(Guid ProjectId, string Name, double Score)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<ClarificationCandidate> candidates)
        => JsonSerializer.Serialize(candidates, Json);

    public static IReadOnlyList<ClarificationCandidate> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ClarificationCandidate>();
        try
        {
            return JsonSerializer.Deserialize<List<ClarificationCandidate>>(json, Json)
                ?? (IReadOnlyList<ClarificationCandidate>)Array.Empty<ClarificationCandidate>();
        }
        catch (JsonException)
        {
            return Array.Empty<ClarificationCandidate>();
        }
    }
}
