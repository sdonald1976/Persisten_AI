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
    /// <summary>Sink for buffered continuation rounds (the transport requires one when streaming).</summary>
    private static readonly IProgress<string> Discard = new NullSink();

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
        string systemPrompt, string userMessage, IProgress<string>? sink = null, string? speaker = null,
        CancellationToken ct = default)
    {
        var full = new StringBuilder();
        string? finishReason = null;
        string? model = null;
        int promptTokens = 0, completionTokens = 0;
        var round = 0;

        for (; ; round++)
        {
            var before = full.ToString();
            var prefix = round == 0 ? null : before;

            // Continuation rounds are never streamed live: a model that repeats instead of
            // continuing would put its duplicate on the user's screen before the guard below
            // could see it. The round is buffered, vetted, and only the new text is forwarded.
            //
            // The first round is also where a "Ava:" prefix would appear, so that is the round
            // that gets watched for one. It has to happen in the stream rather than afterwards:
            // the client keeps the streamed tokens and discards the cleaned reply, and speech is
            // synthesized from them as they arrive.
            var labelSink = round == 0 && sink is not null ? new SelfLabelSink(sink, speaker) : null;
            var liveSink = round == 0 ? labelSink ?? sink : Discard;

            ChatCompletion result;
            try
            {
                result = sink is not null
                    ? await _chat.StreamAsync(systemPrompt, userMessage, liveSink!, prefix, ct)
                    : await _chat.CompleteAsync(systemPrompt, userMessage, jsonMode: false, prefix, ct);
            }
            finally
            {
                // Whatever is still held back must go out, including when the round threw: a reply
                // shorter than the sink's window would otherwise never be delivered at all.
                labelSink?.Flush();
            }

            finishReason = result.FinishReason;
            model ??= result.Model;
            promptTokens += result.PromptTokens ?? 0;
            completionTokens += result.CompletionTokens ?? 0;

            var roundText = result.Text;
            if (round > 0)
            {
                // Models asked to "continue" often re-say the tail of what they already wrote
                // (or restart the whole answer) before adding anything new; drop the echoed part
                // so the reply never reads twice.
                roundText = TextRepetition.TrimLeadingOverlap(before, roundText);

                // Hard stop: a continuation that is just a repeat of what's already there means
                // the model is looping. Drop it entirely — the duplicate must never reach the
                // stored reply — and stop rather than amplify it into a wall of text.
                if (TextRepetition.IsLargelyContained(roundText, before))
                {
                    _logger.LogWarning("Reply from {Model} began repeating on continuation (round {Round}); stopping.",
                        _options.Model, round + 1);
                    break;
                }

                sink?.Report(roundText);
            }

            full.Append(roundText);

            if (!await ShouldContinueAsync(finishReason, full.ToString(), userMessage, round, systemPrompt, ct))
                break;
        }

        // Conversation only, and only at the very end: strip a trailing section she copied from the
        // shape of her own context packet. Stop sequences normally prevent this being generated at
        // all; this is the net for a provider that ignores them. It runs here rather than in the
        // chat model because the structured roles return JSON, which must never be trimmed.
        // Fabricated turns first: everything after an invented "user:" line is not hers, so the
        // structural trim should only ever see her own words.
        // Her own name off the front first, so the trims below only ever see her actual words —
        // and so the stored reply matches what was streamed, instead of teaching the next turn
        // the very format this removes.
        var text = PromptEchoFilter.Trim(
            PromptEchoFilter.TrimFabricatedTurns(
                PromptEchoFilter.TrimStageDirections(
                    PromptEchoFilter.TrimSelfLabel(full.ToString(), speaker))));
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

    /// <summary>
    /// Whether another round could be asked for without the request outgrowing the model's context
    /// window. Estimated the same crude way as every other budget here (~4 chars/token) — the point
    /// is to stop well before the cliff, not to measure it precisely.
    /// </summary>
    private bool FitsInWindow(string systemPrompt, string replySoFar)
    {
        if (_options.ContextTokens <= 0)
            return true; // no window declared — nothing to check against

        var used = (systemPrompt.Length + replySoFar.Length) / 4;
        var room = Math.Max(0, _options.ReplyReserveTokens);
        return used + room <= _options.ContextTokens;
    }

    private async Task<bool> ShouldContinueAsync(
        string? finishReason, string replySoFar, string userMessage, int round, string systemPrompt,
        CancellationToken ct)
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

        // A continuation re-sends the prompt AND everything written so far, so each round costs
        // more window than the last. Left unchecked the request eventually exceeds the model's
        // context, and the server does not refuse it — it discards from the top of the prompt and
        // keeps generating, so the identity and standing rules vanish partway through a reply that
        // is still being written. That is the same silent truncation the prompt budget exists to
        // prevent, arriving from the other end.
        //
        // Stopping with a slightly short answer is a far better failure than continuing into one
        // that quietly stops being hers.
        if (!FitsInWindow(systemPrompt, replySoFar))
        {
            _logger.LogWarning(
                "Reply from {Model} cannot be continued without exceeding the {Context}-token context " +
                "window; stopping at round {Round}. A larger window is the fix if this is common.",
                _options.Model, _options.ContextTokens, round + 1);
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

    private sealed class NullSink : IProgress<string>
    {
        public void Report(string value) { }
    }
}
