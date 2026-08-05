using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Deterministic, offline memory extractor driven by a handful of surface patterns over the
/// user's own messages. It only ever <em>proposes</em> candidates — the pipeline validates
/// them. It is the default provider so the whole system runs with no LLM; swap in
/// <see cref="LlmMemoryExtractor"/> for real language understanding.
/// </summary>
public sealed partial class RuleBasedMemoryExtractor : IMemoryExtractor
{
    public Task<IReadOnlyList<MemoryCandidate>> ExtractAsync(
        string userId, IReadOnlyList<Message> exchange, CancellationToken ct = default)
    {
        var candidates = new List<MemoryCandidate>();

        // Only the user's own statements produce memories, never the assistant's text.
        foreach (var message in exchange.Where(m => m.Role == MessageRole.User))
        {
            foreach (var sentence in SplitSentences(message.Content))
            {
                var candidate = MatchSentence(sentence, message.Id);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
        }

        return Task.FromResult<IReadOnlyList<MemoryCandidate>>(candidates);
    }

    private static MemoryCandidate? MatchSentence(string sentence, Guid messageId)
    {
        var evidence = new[] { new CandidateEvidence(messageId, sentence) };
        var lower = sentence.ToLowerInvariant();

        // Concrete events first — they're less ambiguous than preference phrasing.
        // Completed events.
        var done = Completed().Match(sentence);
        if (done.Success)
        {
            var what = Clean($"{done.Groups[1].Value} {done.Groups[2].Value}");
            return new MemoryCandidate
            {
                Kind = MemoryKind.Episodic,
                Content = $"The user {what}.",
                EpisodeStatus = EpisodeStatus.Resolved,
                ProposedConfidence = 0.8,
                Importance = 0.6,
                Evidence = evidence,
            };
        }

        // Acquisitions (often open loops: awaiting arrival).
        var acquired = Acquired().Match(sentence);
        if (acquired.Success)
        {
            var what = Clean($"{acquired.Groups[1].Value} {acquired.Groups[2].Value}");
            var awaiting = lower.Contains("waiting") || lower.Contains("arrive") || lower.Contains("hasn't come");
            return new MemoryCandidate
            {
                Kind = MemoryKind.Episodic,
                Content = $"The user {what}.",
                EpisodeStatus = awaiting ? EpisodeStatus.Planned : EpisodeStatus.Occurred,
                ProposedConfidence = 0.7,
                Importance = 0.5,
                Evidence = evidence,
            };
        }

        // Temporary state ("I'm eating low carb this week").
        if (lower.Contains("this week") || lower.Contains("this month"))
        {
            var state = State().Match(sentence);
            if (state.Success)
            {
                var value = Clean(state.Groups[1].Value);
                return new MemoryCandidate
                {
                    Kind = MemoryKind.Semantic,
                    Subject = "user",
                    Predicate = "temporary_state",
                    Value = value,
                    Content = $"The user is currently {value} (temporary).",
                    Validity = Validity.Temporary,
                    ProposedConfidence = 0.6,
                    Importance = 0.4,
                    Evidence = evidence,
                };
            }
        }

        // Preference / durable fact.
        var pref = Preference().Match(sentence);
        if (pref.Success)
        {
            var value = Clean(pref.Groups[1].Value);
            var temporary = lower.Contains("this week") || lower.Contains("this month") || lower.Contains("right now");
            return new MemoryCandidate
            {
                Kind = MemoryKind.Semantic,
                Subject = "user",
                Predicate = "prefers",
                Value = value,
                Content = $"The user prefers {value}.",
                Validity = temporary ? Validity.Temporary : Validity.Current,
                ProposedConfidence = temporary ? 0.6 : 0.75,
                Importance = 0.5,
                Evidence = evidence,
            };
        }

        // Intentions / plans (open loops).
        var plan = Planned().Match(sentence);
        if (plan.Success)
        {
            var what = Clean(plan.Groups[1].Value);
            return new MemoryCandidate
            {
                Kind = MemoryKind.Episodic,
                Content = $"The user plans to {what}.",
                EpisodeStatus = EpisodeStatus.Planned,
                ProposedConfidence = 0.65,
                Importance = 0.55,
                Evidence = evidence,
            };
        }

        return null;
    }

    private static IEnumerable<string> SplitSentences(string text)
        => SentenceSplit().Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    private static string Clean(string value)
    {
        value = Whitespace().Replace(value.Trim(), " ");
        return value.TrimEnd('.', '!', '?', ',', ';').Trim();
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bI (?:like|prefer|love|enjoy)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Preference();

    [GeneratedRegex(@"\bI(?:'m| am)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex State();

    [GeneratedRegex(@"\bI (deployed|tested|finished|completed|shipped|released|built)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Completed();

    [GeneratedRegex(@"\bI (ordered|bought|got|purchased|received)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Acquired();

    [GeneratedRegex(@"\bI (?:plan to|planned to|am going to|will|need to|want to)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex Planned();
}
