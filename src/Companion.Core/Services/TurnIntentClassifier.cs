using System.Text.RegularExpressions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Classifies what Ava should DO this turn, deterministically, from the working-context read
/// and this turn's retrieval â€” it consumes <see cref="WorkingContextState"/> rather than
/// re-deriving any of it. No model call: the ToolNudge lesson says a deterministic rule and a
/// small model are BOTH presumed wrong until captured data says otherwise, and the cheap one
/// should be measured first.
///
/// The vocabulary is small and grounded in observed conversational needs, not a speech-act
/// taxonomy. Selection requires a confidence bar; below it the answer is "unknown", which
/// means "continue naturally" â€” preferred over a confidently wrong classification, because in
/// shadow a wrong "unknown" costs a corpus row while a wrong intent would one day cost a turn.
/// </summary>
public static partial class TurnIntentClassifier
{
    /// <summary>Above follow-topic-change/acknowledge (a directive outranks the topic-shape
    /// reading of the same words), below answer-question (a question-form request â€” "can you
    /// remind meâ€¦?" â€” is answered by performing it, and answer-question already says so).</summary>
    private const double DirectiveConfidence = 0.7;

    /// <summary>Below this, the selection is "unknown" rather than the best weak guess.</summary>
    private const double SelectionBar = 0.6;

    public static TurnIntentState Classify(
        WorkingContextState working, string userMessage, int memoriesRetrieved)
    {
        var message = userMessage.Trim();
        var isQuestion = message.EndsWith('?');
        var candidates = new List<IntentCandidate>();

        // Added in every branch (an imperative can carry a question mark: "Can you remind
        // meâ€¦?" â€” there, answer-question outranks it and answering IS performing).
        if (LooksDirective(message))
            candidates.Add(new(TurnIntent.RequestDirective, DirectiveConfidence,
                "imperative/request shape â€” perform the requested act"));

        // Move-grounded intents first â€” these come from working context's read of the turn.
        if (working.Move == ConversationMove.Correction)
            candidates.Add(new(TurnIntent.AcceptCorrection, 0.85, "the message is a correction of something recent"));
        if (working.Move == ConversationMove.AnswersOpenQuestion)
            candidates.Add(new(TurnIntent.RespondToAnswer, 0.85,
                $"the message answers her question: \"{working.BoundQuestion}\""));

        if (isQuestion)
        {
            if (working.ResolutionConfidence == ResolutionConfidence.Guess || working is { ReferenceMarkers.Count: > 0, ResolvedReference: null })
            {
                // The question depends on a reference the system could not pin â€” answering
                // means guessing, and one short question is cheaper than a wrong answer.
                candidates.Add(new(TurnIntent.Clarify, 0.75,
                    $"the question depends on \"{working.ReferenceMarkers.FirstOrDefault()}\", which is ambiguous here"));
                candidates.Add(new(TurnIntent.AnswerQuestion, 0.5, "it is still a question"));
            }
            else if (ProgressQuestion().IsMatch(message) && memoriesRetrieved == 0)
            {
                // Asking how something of theirs is going, with nothing retrieved to answer
                // from: the honest act is admitting she can't see it. The documented failure
                // is three paragraphs of invented compost layers.
                candidates.Add(new(TurnIntent.AdmitUnknown, 0.7,
                    "a progress question with nothing retrieved to answer from"));
                candidates.Add(new(TurnIntent.AnswerQuestion, 0.4, "it is still a question"));
            }
            else
            {
                candidates.Add(new(TurnIntent.AnswerQuestion, 0.8, "the user asked a question"));
            }
        }
        else
        {
            if (Interjection().IsMatch(message)
                && working.Move != ConversationMove.AnswersOpenQuestion)
            {
                // "ok" / "lol" with no question of hers in play carries no act to classify.
                // Deliberately nothing added: unknown is the honest label.
            }
            else if (FirstPersonShare().IsMatch(message))
            {
                candidates.Add(new(TurnIntent.Acknowledge, 0.7, "the user is sharing something of their own"));
            }

            if (working.Move is ConversationMove.ContinuesThread or ConversationMove.ResolvesReference)
                candidates.Add(new(TurnIntent.ContinueTopic, 0.65, "the thread continues"));
            if (working.Move == ConversationMove.NewTopic && !Interjection().IsMatch(message))
                candidates.Add(new(TurnIntent.FollowTopicChange, 0.6, "the subject changed; follow it"));

            // An ambiguous reference in a STATEMENT does not block replying, so clarify is
            // offered as a competing candidate only â€” the shadow data will say whether it
            // deserves more.
            if (working.ResolutionConfidence == ResolutionConfidence.Guess)
                candidates.Add(new(TurnIntent.Clarify, 0.5,
                    $"\"{working.ReferenceMarkers.FirstOrDefault()}\" is ambiguous, though a reply doesn't require resolving it"));
        }

        var ordered = candidates.OrderByDescending(c => c.Confidence).ToList();
        var top = ordered.FirstOrDefault();

        if (top is null || top.Confidence < SelectionBar)
        {
            return new TurnIntentState
            {
                Intent = TurnIntent.Unknown,
                Confidence = top?.Confidence ?? 0.0,
                Reason = top is null
                    ? "no rule matched â€” continue naturally"
                    : $"best candidate ({top.Intent.ToKebab()}) below the bar â€” continue naturally",
                Candidates = ordered,
            };
        }

        return new TurnIntentState
        {
            Intent = top.Intent,
            Confidence = top.Confidence,
            Reason = top.Reason,
            Candidates = ordered,
        };
    }

    /// <summary>An imperative or polite-request shape. Public because the capture path tags
    /// inputs with it â€” the corpus that decides whether request/directive joins the
    /// vocabulary needs the flag on every turn, not only the ones this file classified.</summary>
    public static bool LooksDirective(string message) => DirectiveShape().IsMatch(message.Trim());

    /// <summary>Bare-verb openers and polite requests. One general shape on purpose â€” the
    /// instruction is to find out whether ONE request act is warranted, not to grow a
    /// taxonomy of commands.</summary>
    [GeneratedRegex(
        @"^(please\s+)?(ask|tell|give|show|help|remind|describe|explain|list|suggest|recommend|" +
        @"name|pick|choose|write|draft|find|check|walk me|talk me|let's|don't|do not|stop|wait|" +
        @"hold|skip|never mind|forget|ignore|imagine|pretend|play|try|keep|start|stay|share|" +
        @"read|summari[sz]e|say|sing|make|look)\b|^(can|could|would|will) you\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveShape();

    /// <summary>"How's X coming along / going" â€” a status question about the user's own world.</summary>
    [GeneratedRegex(@"\b(how('s| is| are| was| did)\b|coming along|any progress|going (with|on with))",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProgressQuestion();

    [GeneratedRegex(@"^(I('m|'ve|'ll|'d)?|My|We('re|'ve)?|Our)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPersonShare();

    [GeneratedRegex(@"^(ok(ay)?|k+|lol|ha(ha)*|hm+|yeah|yep|yes|nah|no|nice|cool|fair|sure|right|wow|oof|ugh|thanks|ty|cheers)[.!\s]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Interjection();
}


