using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Asks a small, fast model one yes/no question — "is this reply finished, or cut off?" — so a
/// self-truncating model gets caught even though the transport reported a normal <c>"stop"</c>.
/// Runs on the summarizer/extraction endpoint (cheap), never the big conversational one. Fails
/// closed: any parse failure, empty answer, or error is treated as COMPLETE, so a flaky judge can
/// never trap the user in runaway continuation.
/// </summary>
public sealed class LlmCompletionJudge : ICompletionJudge
{
    private const string SystemPrompt =
        "You are a strict completion checker. Given what a user asked for and the assistant's reply " +
        "so far, decide whether the reply FULLY answers the request or was cut off / left unfinished " +
        "(a story or plan that clearly stops partway, a sentence that ends mid-thought, an explicit " +
        "offer to continue). Answer with exactly one word: COMPLETE or CONTINUE. No other text.";

    // Guard rails: never feed an unbounded reply to the judge, and never let its own answer run long.
    private const int MaxReplyChars = 12000;

    private readonly IChatModel _judge;
    private readonly ILogger<LlmCompletionJudge> _logger;

    public LlmCompletionJudge(IChatModel judge, ILogger<LlmCompletionJudge> logger)
    {
        _judge = judge;
        _logger = logger;
    }

    public async Task<bool> IsCompleteAsync(string userRequest, string replySoFar, CancellationToken ct = default)
    {
        var reply = replySoFar.Length > MaxReplyChars ? replySoFar[^MaxReplyChars..] : replySoFar;
        var prompt =
            $"User's request:\n{userRequest}\n\n" +
            $"Assistant's reply so far:\n{reply}\n\n" +
            "Is this reply COMPLETE or should it CONTINUE?";

        try
        {
            var verdict = await _judge.CompleteAsync(SystemPrompt, prompt, ct: ct);
            var answer = verdict.Text.Trim();

            // Fail closed: only an explicit, unambiguous CONTINUE keeps generation going.
            var continueRequested =
                answer.Contains("CONTINUE", StringComparison.OrdinalIgnoreCase) &&
                !answer.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("Completion judge verdict: {Verdict} → {Decision}",
                answer.Length > 40 ? answer[..40] + "…" : answer, continueRequested ? "CONTINUE" : "COMPLETE");
            return !continueRequested;
        }
        catch (Exception ex)
        {
            // A judge failure must never block the user's reply — treat as complete.
            _logger.LogWarning(ex, "Completion judge failed; treating the reply as complete.");
            return true;
        }
    }
}
