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
        string userId, IReadOnlyList<Message> exchange,
        ReferenceResolution? resolution = null, CancellationToken ct = default)
    {
        var candidates = new List<MemoryCandidate>();

        // Only the user's own statements produce memories, never the assistant's text.
        foreach (var message in exchange.Where(m => m.Role == MessageRole.User))
        {
            // A project named anywhere in the message tags every candidate the message produces:
            // "I'm working on Halyard. I need to finish the export job." is two sentences about
            // one project, and only the first one says so.
            var project = DetectProject(message.Content);

            foreach (var sentence in SplitSentences(message.Content))
            {
                var candidate = MatchSentence(sentence, message.Id);
                if (candidate is null)
                    continue;
                candidates.Add(project is null ? candidate : candidate with { RelatedProject = project });
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

        // Ongoing work. This is the phrasing that names a project, so without it the project-birth
        // path has nothing to attach a name to — the sentence that says what the user is working
        // on produced no candidate at all, and the project stayed invisible.
        var working = WorkingOn().Match(sentence);
        if (working.Success)
        {
            var what = Clean(working.Groups[1].Value);
            return new MemoryCandidate
            {
                Kind = MemoryKind.Episodic,
                Content = $"The user is working on {what}.",
                EpisodeStatus = EpisodeStatus.InProgress,
                ProposedConfidence = 0.7,
                Importance = 0.6,
                Evidence = evidence,
            };
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

    /// <summary>
    /// Pulls a project name out of an explicit naming phrase ("a project called X", "working on X").
    /// Deliberately narrow: this feeds the project-birth path, and inventing a project from a vague
    /// mention is far worse than missing one — the LLM extractor is the path that generalizes.
    /// </summary>
    private static string? DetectProject(string text)
    {
        var match = ProjectCalled().Match(text);
        if (!match.Success)
            match = ProjectWorkingOn().Match(text);
        if (!match.Success)
            return null;

        // "Halyard — it's a C# service" names the project and then describes it. A spaced dash
        // separates the two; an unspaced one can be part of the name ("claude-code").
        var name = DashSeparator().Split(match.Groups["name"].Value, 2)[0];
        name = Clean(name).TrimEnd('-', ':', ';').Trim();

        // "working on it" / "working on the thing" is a reference, not a name.
        if (name.Length < MinProjectName || name.Length > MaxProjectName)
            return null;
        return NonName().IsMatch(name) ? null : name;
    }

    private const int MinProjectName = 3;
    private const int MaxProjectName = 60;

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

    [GeneratedRegex(@"\b(?:I(?:'m| am)|we(?:'re| are)) working on\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkingOn();

    // Tried in this order, not as one alternation: "I'm working on a project called X" contains
    // both triggers, and the leftmost match would otherwise win and capture "a project called X".
    [GeneratedRegex(
        @"\bprojects? (?:called|named)\s+(?<name>[^.,;!?\n]{3,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectCalled();

    // "the" is excluded so "working on the export job" doesn't become a project of its own.
    [GeneratedRegex(
        @"\b(?:I(?:'m| am)|we(?:'re| are)) working on\s+(?!the\b)(?<name>[^.,;!?\n]{3,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectWorkingOn();

    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex DashSeparator();

    // Pronouns and generic nouns that follow the trigger without naming anything.
    [GeneratedRegex(
        @"^(?:it|that|this|them|those|stuff|things?|something|anything|work|a lot|my project|the project)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonName();
}
