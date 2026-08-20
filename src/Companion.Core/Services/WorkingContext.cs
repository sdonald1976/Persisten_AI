using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Reads the recent transcript into an explicit <see cref="WorkingContextState"/>: what
/// questions are hanging, what is being discussed, which entities are salient, what the user's
/// references point at, what kind of turn this is, and what retrieval should therefore search
/// for. Everything here is deterministic string work over the messages already in hand — no
/// model calls, no storage, no new prompt sections beyond the one interpretation note.
///
/// The rules are deliberately conservative (the ToolNudge lesson: heuristics score 0.778 on
/// the phrasings their author imagined and 0.087 on real ones), and their verdicts are recorded
/// per turn and captured for corpus review, so the real hit rate gets measured, not assumed.
/// A wrong CLASSIFICATION costs little — a note is only asserted on the confident shapes, and
/// the retrieval query falls back to the raw message whenever nothing resolves.
/// </summary>
public static partial class WorkingContext
{
    /// <summary>Moves, as the stable strings the diagnostics ring reports.</summary>
    public static class Moves
    {
        public const string AnswersOpenQuestion = "answers-open-question";
        public const string ResolvesReference = "resolves-reference";
        public const string Correction = "correction";
        public const string ContinuesThread = "continues-thread";
        public const string NewTopic = "new-topic";
    }

    private const int MaxOpenQuestions = 3;
    private const int MaxEntities = 5;
    private const int MaxReferentChars = 160;

    public static WorkingContextState Read(
        IReadOnlyList<Message> recent,
        string userMessage,
        string? resolvedProject = null,
        string? userName = null,
        string? companionName = null)
    {
        var message = userMessage.Trim();
        var allEntities = SalientEntities(recent, userName, companionName);
        var entities = allEntities.Select(e => e.Value).ToList();
        var userEntities = allEntities.Where(e => e.Source == MessageRole.User).Select(e => e.Value).ToList();
        var openQuestions = OpenQuestions(recent);
        var topic = Topic(recent, resolvedProject);

        var markers = new List<string>();
        var binding = AnswerBindingDetector.Detect(recent, userMessage);

        // Reference resolution, most specific first. Resolution, classification, and
        // ASSERTION are three different confidence bars: a marker can be detected and fail to
        // resolve (classified only); resolve from a guess like "her" → most recent entity
        // (query rewritten, nothing asserted to the model); or resolve from something exact
        // like an enumerated item (query rewritten AND the packet told). Only the last earns
        // an authoritative note — being wrong in the packet is worse than being silent.
        string? marker = null, referent = null;
        var assertive = false;
        if (OrdinalReference().Match(message) is { Success: true } ordinal)
        {
            marker = ordinal.Value;
            referent = ResolveOrdinal(ordinal.Groups["which"].Value, recent);
            assertive = referent is not null;
            markers.Add(marker);
        }
        else if (ThatOneReference().Match(message) is { Success: true } thatOne)
        {
            marker = thatOne.Value;
            referent = entities.FirstOrDefault();
            markers.Add(marker);
        }
        else if (PersonPronoun().Match(message) is { Success: true } pronoun)
        {
            // Prefer entities the USER introduced: the first live run resolved "her" to a name
            // lifted from the companion's own reply while the person the user had just named
            // sat one message earlier. People the user brings up are who their pronouns mean.
            marker = pronoun.Value;
            referent = (userEntities.FirstOrDefault() ?? entities.FirstOrDefault());
            markers.Add(marker);
        }
        else if (SaidBefore().Match(message) is { Success: true } before)
        {
            marker = before.Value;
            referent = PreviousSubstantiveUserMessage(recent);
            assertive = referent is not null;
            markers.Add(marker);
        }

        // ---- classify the move, highest-precedence first ----
        string move;
        string? note = null, boundQuestion = null;
        var query = userMessage;

        if (IsCorrection(message))
        {
            move = Moves.Correction;
            note = Prompts.Get("interpretation.correction");
        }
        else if (binding is not null)
        {
            move = Moves.AnswersOpenQuestion;
            boundQuestion = binding.Question;
            query = referent is null
                ? $"{binding.Question} {binding.Answer}"
                : $"{binding.Question} {binding.Answer} {referent}";
            note = assertive && referent is not null
                ? Prompts.Format("interpretation.reference",
                    ("marker", marker!), ("referent", referent))
                : Prompts.Format("interpretation.answer-binding",
                    ("answer", binding.Answer), ("question", binding.Question));
        }
        else if (referent is not null)
        {
            move = Moves.ResolvesReference;
            query = $"{userMessage} {referent}";
            note = assertive
                ? Prompts.Format("interpretation.reference",
                    ("marker", marker!), ("referent", referent))
                : null;
        }
        else if (markers.Count > 0 || ContentOverlap(message, LastExchangeText(recent)) >= 2)
        {
            // A detected-but-unresolved reference, or real word overlap with the last
            // exchange: the thread continues, but nothing is confidently known beyond that,
            // so classify without asserting a note.
            move = Moves.ContinuesThread;
        }
        else
        {
            move = Moves.NewTopic;
        }

        // A question the current turn just answered is no longer open.
        if (boundQuestion is not null)
            openQuestions = openQuestions.Where(q => q.Question != boundQuestion).ToList();

        return new WorkingContextState
        {
            OpenQuestions = openQuestions,
            Topic = topic,
            SalientEntities = entities,
            ReferenceMarkers = markers,
            Move = move,
            ResolvedReference = referent,
            BoundQuestion = boundQuestion,
            RawQuery = userMessage,
            RetrievalQuery = query,
            InterpretationNote = note,
        };
    }

    // ---- open questions ----

    /// <summary>
    /// Trailing questions from the companion's messages in the window that no later user
    /// message addressed — by elliptical binding or by sharing two content words with the
    /// question. Newest first. This tells the system (not yet the prompt) what she has asked
    /// and is still owed; surfacing it in the packet is a separate decision with a nag risk.
    /// </summary>
    private static IReadOnlyList<OpenQuestionState> OpenQuestions(IReadOnlyList<Message> recent)
    {
        var open = new List<OpenQuestionState>();
        for (var i = recent.Count - 1; i >= 0 && open.Count < MaxOpenQuestions; i--)
        {
            if (recent[i].Role != MessageRole.Assistant)
                continue;
            if (AnswerBindingDetector.TrailingQuestion(recent[i].Content) is not { } question)
                continue;

            var answered = false;
            for (var j = i + 1; j < recent.Count && !answered; j++)
            {
                if (recent[j].Role != MessageRole.User)
                    continue;
                answered = AnswerBindingDetector.Detect(new[] { recent[i] }, recent[j].Content) is not null
                    || ContentOverlap(recent[j].Content, question) >= 2;
            }
            if (!answered)
                open.Add(new OpenQuestionState(question, recent.Count - i));
        }
        return open;
    }

    // ---- topic and entities ----

    private static string? Topic(IReadOnlyList<Message> recent, string? resolvedProject)
    {
        if (!string.IsNullOrWhiteSpace(resolvedProject))
            return resolvedProject;

        var lastSubstantive = recent.LastOrDefault(
            m => m.Role == MessageRole.User && ContentWords(m.Content).Count >= 2);
        if (lastSubstantive is null)
            return null;

        var words = ContentWords(lastSubstantive.Content).Take(3).ToList();
        return words.Count == 0 ? null : string.Join(" ", words);
    }

    /// <summary>
    /// Capitalized tokens (and runs of them: "Marsh Lane") that don't open a sentence, newest
    /// first, excluding the two speakers and calendar words, tagged with who said them. A crude
    /// proper-noun read — its misses cost nothing (no resolution happens), and its hits give
    /// "her" and "that one" something concrete to point at.
    /// </summary>
    private static IReadOnlyList<(string Value, MessageRole Source)> SalientEntities(
        IReadOnlyList<Message> recent, string? userName, string? companionName)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<(string Value, MessageRole Source)>();
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            foreach (Match m in EntityCandidate().Matches(recent[i].Content))
            {
                var value = TrimFunctionWords(m.Value,
                    startsSentence: StartsSentence(recent[i].Content, m.Index));
                if (value is null)
                    continue;
                if (CalendarWord().IsMatch(value)
                    || value.Equals(userName, StringComparison.OrdinalIgnoreCase)
                    || value.Equals(companionName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.TryGetValue(value, out var at))
                {
                    // Names travel: the user says "Beth", the companion's reply repeats it,
                    // and the newest mention is hers. Who a name BELONGS to for pronoun
                    // purposes is whoever ever said it as the user, so a user mention
                    // upgrades the tag wherever the name was first kept.
                    if (recent[i].Role == MessageRole.User && entities[at].Source != MessageRole.User)
                        entities[at] = (entities[at].Value, MessageRole.User);
                    continue;
                }
                seen[value] = entities.Count;
                entities.Add((value, recent[i].Role));
            }
        }
        return entities.Take(MaxEntities).ToList();
    }

    /// <summary>
    /// Sheds capitalized function words from the front of a candidate ("Will Precious" →
    /// "Precious" — the first live run resolved a pronoun to that auxiliary-plus-name), and
    /// rejects sentence-case single words at sentence starts, which are indistinguishable from
    /// ordinary prose. Null when nothing survives.
    /// </summary>
    private static string? TrimFunctionWords(string candidate, bool startsSentence)
    {
        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var dropped = 0;
        while (tokens.Count > 0 && FunctionWords.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
            dropped++;
        }
        if (tokens.Count == 0)
            return null;

        // A single sentence-case word opening a sentence is indistinguishable from prose
        // ("Beth arrived" vs "Suddenly it rained") — reject it. A multi-word run there is a
        // real name ("Beth Miller called"), and a run whose auxiliary was shed keeps the rest
        // ("Will Precious get to…" → "Precious").
        if (startsSentence && dropped == 0 && tokens.Count == 1)
            return null;

        return string.Join(' ', tokens);
    }

    /// <summary>Capitalized words that open questions and clauses, not name anything. The cost
    /// of listing "Will" is a person named Will at a sentence head — accepted: a missed entity
    /// resolves nothing, while a false one misdirected a pronoun in the first live run.</summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "Will", "Would", "Should", "Could", "Can", "May", "Might", "Must", "Shall",
        "How", "What", "When", "Where", "Why", "Who", "Which", "Whose",
        "Is", "Are", "Was", "Were", "Do", "Does", "Did", "Has", "Have", "Had",
        "The", "A", "An", "And", "But", "Or", "If", "So", "Then", "That", "This",
        "I", "Let", "Also", "Just", "Now", "Anyway", "Oh", "Well", "Yes", "No",
        "Okay", "Thanks", "Please", "Maybe", "Perhaps",
    };

    private static bool StartsSentence(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '(' or '*' or '-')
                continue;
            return c is '.' or '!' or '?' or ':' or '\n';
        }
        return true;
    }

    // ---- reference resolution ----

    /// <summary>
    /// "the second one" against whatever the companion's last message enumerated: bulleted or
    /// numbered lines first, else the comma/or options inside its trailing question
    /// ("coffee, tea, or chocolate?").
    /// </summary>
    private static string? ResolveOrdinal(string which, IReadOnlyList<Message> recent)
    {
        var last = recent.LastOrDefault(m => m.Role == MessageRole.Assistant);
        if (last is null)
            return null;

        var items = EnumeratedItems(last.Content);
        if (items.Count < 2)
            return null;

        var index = which.ToLowerInvariant() switch
        {
            "first" => 0, "second" => 1, "third" => 2, "fourth" => 3, "fifth" => 4,
            "last" => items.Count - 1,
            _ => -1,
        };
        return index >= 0 && index < items.Count ? Clip(items[index]) : null;
    }

    private static IReadOnlyList<string> EnumeratedItems(string text)
    {
        var lines = text.Split('\n')
            .Select(l => ListItem().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["item"].Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (lines.Count >= 2)
            return lines;

        if (AnswerBindingDetector.TrailingQuestion(text) is { } question
            && question.Contains(" or ", StringComparison.OrdinalIgnoreCase))
        {
            var options = question.TrimEnd('?')
                .Split([",", " or "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Trim())
                .Where(o => o.Length is > 0 and <= 60)
                .ToList();
            // The lead-in ("Which do you prefer: coffee") rides on the first option; keep only
            // what follows the last colon or interrogative head.
            if (options.Count >= 2)
            {
                var head = options[0];
                var colon = head.LastIndexOf(':');
                if (colon >= 0 && colon + 1 < head.Length)
                    options[0] = head[(colon + 1)..].Trim();
                return options.Where(o => o.Length > 0).ToList();
            }
        }
        return Array.Empty<string>();
    }

    private static string? PreviousSubstantiveUserMessage(IReadOnlyList<Message> recent)
    {
        var msg = recent.LastOrDefault(m => m.Role == MessageRole.User && m.Content.Trim().Length >= 20);
        return msg is null ? null : Clip(msg.Content.Trim());
    }

    // ---- move helpers ----

    /// <summary>
    /// Correction markers, gated to the START of the message: "actually" or "no," mid-thought
    /// is ordinary English; leading it is a repair. "I meant" corrects wherever it appears.
    /// </summary>
    private static bool IsCorrection(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.StartsWith("actually,") || lower.StartsWith("actually ")
            || lower.StartsWith("no, ") || lower.StartsWith("no - ") || lower.StartsWith("no — ")
            || lower.StartsWith("wait, ") || lower.StartsWith("wait - ")
            || lower.Contains("i meant") || lower.Contains("i didn't say") || lower.Contains("that's not what i");
    }

    private static string LastExchangeText(IReadOnlyList<Message> recent)
    {
        var take = Math.Min(2, recent.Count);
        return string.Join(" ", recent.Skip(recent.Count - take).Select(m => m.Content));
    }

    private static int ContentOverlap(string a, string b)
        => ContentWords(a).Intersect(ContentWords(b), StringComparer.OrdinalIgnoreCase).Count();

    private static IReadOnlyList<string> ContentWords(string text)
        => Word().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 3 && !Stopwords.Contains(w))
            .Distinct()
            .ToList();

    private static string Clip(string text)
        => text.Length <= MaxReferentChars ? text : text[..MaxReferentChars] + "…";

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "these", "those", "with", "from", "have", "been", "were", "will",
        "would", "could", "should", "about", "there", "their", "they", "them", "then", "than",
        "what", "when", "where", "which", "while", "your", "yours", "mine", "just", "like",
        "really", "very", "some", "something", "anything", "nothing", "everything", "going",
        "want", "know", "think", "thing", "things", "yeah", "okay", "sure", "well", "also",
        "still", "over", "into", "onto", "does", "doing", "done", "much", "many", "more",
        "most", "other", "another", "because", "before", "after", "right", "good", "great",
    };

    [GeneratedRegex(@"\bthe (?<which>first|second|third|fourth|fifth|last) one\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalReference();

    [GeneratedRegex(@"\b(that|this) one\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThatOneReference();

    /// <summary>Object-position person pronouns only. "it"/"they" are everywhere in ordinary
    /// English and resolve to almost anything; "her"/"him" nearly always point at a person
    /// recently named.</summary>
    [GeneratedRegex(@"\b(her|him)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonPronoun();

    [GeneratedRegex(@"\bwhat i (said|told you|mentioned) (before|earlier)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaidBefore();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+(?<item>.+)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b")]
    private static partial Regex EntityCandidate();

    [GeneratedRegex(@"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|January|February|March|April|May|June|July|August|September|October|November|December|Today|Tomorrow|Yesterday)$", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarWord();

    [GeneratedRegex(@"[a-zA-Z']+")]
    private static partial Regex Word();
}
