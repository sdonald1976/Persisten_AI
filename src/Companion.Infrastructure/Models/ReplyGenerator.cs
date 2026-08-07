using System.Text;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Owns the "when to keep going" decision for a turn (see <see cref="IReplyGenerator"/>). Each round
/// it calls the transport once, then decides whether the reply is finished:
///
///   1. <c>finish_reason == "length"</c> → the server cut it off mid-answer; continue (transport truth).
///   2. otherwise (a natural <c>"stop"</c>) → if the reply is long enough that "unfinished" is
///      plausible, ask the <see cref="ICompletionJudge"/>; continue only if it says CONTINUE.
///   3. otherwise → done.
///
/// On every continuation it feeds the text produced so far back to the transport, so the model
/// resumes the <em>same</em> answer rather than starting a new one. Bounded by
/// <see cref="EndpointOptions.MaxContinuations"/> so it can never run away.
/// </summary>
public sealed class ReplyGenerator : IReplyGenerator
{
    private readonly IChatModel _chat;
    private readonly ICompletionJudge _judge;
    private readonly EndpointOptions _options;
    private readonly ILogger<ReplyGenerator> _logger;

    public ReplyGenerator(
        IChatModel chat, ICompletionJudge judge, EndpointOptions options, ILogger<ReplyGenerator> logger)
    {
        _chat = chat;
        _judge = judge;
        _options = options;
        _logger = logger;
    }

    public async Task<ChatCompletion> GenerateAsync(
        string systemPrompt, string userMessage, IProgress<string>? sink = null, CancellationToken ct = default)
    {
        var full = new StringBuilder();
        string? finishReason = null;
        string? model = null;
        int promptTokens = 0, completionTokens = 0;
        var round = 0;

        for (; ; round++)
        {
            var prefix = round == 0 ? null : full.ToString();
            var before = full.ToString();

            var result = sink is not null
                ? await _chat.StreamAsync(systemPrompt, userMessage, sink, prefix, ct)
                : await _chat.CompleteAsync(systemPrompt, userMessage, jsonMode: false, prefix, ct);

            full.Append(result.Text);
            finishReason = result.FinishReason;
            model ??= result.Model;
            promptTokens += result.PromptTokens ?? 0;
            completionTokens += result.CompletionTokens ?? 0;

            // Hard stop: if a continuation just repeats what's already there, the model is looping —
            // never keep going and amplify it into a wall of duplicated text.
            if (round > 0 && TextRepetition.IsLargelyContained(result.Text, before))
            {
                _logger.LogWarning("Reply from {Model} began repeating on continuation (round {Round}); stopping.",
                    _options.Model, round + 1);
                break;
            }

            if (!await ShouldContinueAsync(finishReason, full.ToString(), userMessage, round, ct))
                break;
        }

        var text = full.ToString();
        var truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        return new ChatCompletion
        {
            Text = string.IsNullOrWhiteSpace(text) ? "(the model returned an empty response)" : text.Trim(),
            FinishReason = finishReason,
            Rounds = round + 1,
            Truncated = truncated,
            Model = model,
            PromptTokens = promptTokens > 0 ? promptTokens : null,
            CompletionTokens = completionTokens > 0 ? completionTokens : null,
        };
    }

    private async Task<bool> ShouldContinueAsync(
        string? finishReason, string replySoFar, string userMessage, int round, CancellationToken ct)
    {
        if (!_options.AutoContinue)
            return false;

        if (round >= _options.MaxContinuations)
        {
            _logger.LogWarning(
                "Reply from {Model} still not finished after {Rounds} continuation(s); stopping (raise MaxTokens or MaxContinuations).",
                _options.Model, round + 1);
            return false;
        }

        // 1. Transport truth: the server genuinely cut the reply off mid-token. Always finish it —
        //    this is never the runaway case (that was self-stop + an over-eager judge).
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Reply from {Model} was cut off (finish_reason=length); continuing (round {Round}).",
                _options.Model, round + 1);
            return true;
        }

        // The reply stopped on its own. Only *deliverable* requests are ever chased to completion —
        // ordinary conversation is done whenever the model decided it was, so it's never continued.
        if (!CompletionSignals.IsDeliverableRequest(userMessage))
            return false;

        // 2. Deterministic structural check: a deliverable that visibly stops mid-sentence, with an
        //    open code fence, a dangling colon, or a "want me to continue?" — continue, no model call.
        if (CompletionSignals.LooksUnfinished(replySoFar))
        {
            _logger.LogDebug("Deliverable reply from {Model} looks structurally unfinished; continuing (round {Round}).",
                _options.Model, round + 1);
            return true;
        }

        // 3. Last resort: the deliverable looks complete but might be a grammatical self-truncation.
        //    Only here, and only when explicitly enabled, do we spend the (fallible) semantic judge.
        if (!_options.CompletionCheck || replySoFar.Length < _options.CompletionCheckMinChars)
            return false;

        var complete = await _judge.IsCompleteAsync(userMessage, replySoFar, ct);
        if (!complete)
            _logger.LogDebug("Completion judge says the deliverable reply to {Model} is unfinished; continuing (round {Round}).",
                _options.Model, round + 1);
        return !complete;
    }
}
