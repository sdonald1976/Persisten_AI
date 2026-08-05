using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Normalizes candidates into a consistent shape before comparison: collapses whitespace,
/// lowercases subject/predicate, trims values, and derives content when missing. Also
/// produces the keys used for in-batch and against-existing duplicate detection.
/// </summary>
public static partial class MemoryNormalizer
{
    public static MemoryCandidate Normalize(MemoryCandidate candidate)
    {
        var subject = Collapse(candidate.Subject)?.ToLowerInvariant();
        var predicate = Collapse(candidate.Predicate)?.ToLowerInvariant();
        var value = Collapse(candidate.Value);
        var project = Collapse(candidate.RelatedProject);

        var content = Collapse(candidate.Content);
        if (string.IsNullOrEmpty(content))
        {
            content = candidate.Kind == MemoryKind.Semantic
                ? Collapse($"{subject} {predicate} {value}")
                : value;
        }

        return candidate with
        {
            Subject = subject,
            Predicate = predicate,
            Value = value,
            RelatedProject = project,
            Content = content ?? string.Empty,
            Importance = Math.Clamp(candidate.Importance, 0.0, 1.0),
            ProposedConfidence = Math.Clamp(candidate.ProposedConfidence, 0.0, 1.0),
        };
    }

    /// <summary>The "slot" a semantic fact occupies (subject+predicate), used to spot updates.</summary>
    public static string SemanticSlotKey(string? subject, string? predicate)
        => $"{subject?.Trim().ToLowerInvariant()}|{predicate?.Trim().ToLowerInvariant()}";

    /// <summary>A full-equality key for semantic facts (slot + value).</summary>
    public static string SemanticValueKey(string? subject, string? predicate, string? value)
        => $"{SemanticSlotKey(subject, predicate)}|{value?.Trim().ToLowerInvariant()}";

    /// <summary>A coarse key for episodic events, based on normalized content.</summary>
    public static string EpisodicKey(string content)
        => Collapse(content)?.ToLowerInvariant() ?? string.Empty;

    private static string? Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return Whitespace().Replace(text.Trim(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
