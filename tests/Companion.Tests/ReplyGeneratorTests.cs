using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The "when to keep going" policy (<see cref="ReplyGenerator"/>): it continues a reply cut off by
/// the token limit (<c>finish_reason: "length"</c>), asks the completion judge about a reply that
/// stopped on its own, and feeds the text so far back each round so the model resumes the SAME task.
/// The transport and the judge are stubbed — this tests the decision logic, not HTTP.
/// </summary>
public class ReplyGeneratorTests
{
    private static ChatCompletion Cc(string text, string finish) =>
        new() { Text = text, FinishReason = finish };

    private static EndpointOptions Options(
        bool autoContinue = true, int maxContinuations = 6,
        bool completionCheck = true, int minChars = 1) => new()
    {
        Model = "m",
        AutoContinue = autoContinue,
        MaxContinuations = maxContinuations,
        CompletionCheck = completionCheck,
        CompletionCheckMinChars = minChars,
    };

    private static ReplyGenerator Gen(IChatModel chat, ICompletionJudge judge, EndpointOptions opts) =>
        new(chat, judge, opts, NullLogger<ReplyGenerator>.Instance);

    // ---- transport-level truncation (finish_reason: length) ----

    [Fact]
    public async Task Continues_WhileCutOffByTokenLimit_ThenFinishes()
    {
        var chat = new QueuedChatModel(
            Cc("Once upon a time, ", "length"),
            Cc("the buoy bobbed on. ", "length"),
            Cc("The end.", "stop"));

        var result = await Gen(chat, new AlwaysCompleteJudge(), Options()).GenerateAsync("sys", "write a story");

        Assert.Equal("Once upon a time, the buoy bobbed on. The end.", result.Text);
        Assert.Equal(3, chat.Calls);
        Assert.Equal(3, result.Rounds);
        Assert.Equal("stop", result.FinishReason);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task FeedsTheTextSoFarBack_SoItResumesTheSameTask()
    {
        var chat = new QueuedChatModel(
            Cc("Chapter one. ", "length"),
            Cc("Chapter two.", "stop"));

        await Gen(chat, new AlwaysCompleteJudge(), Options()).GenerateAsync("sys", "write a story");

        Assert.Null(chat.Prefixes[0]);                          // first round: no prefix
        Assert.Contains("Chapter one.", chat.Prefixes[1]!);     // second round resumes from what was written
    }

    [Fact]
    public async Task StopsAtTheContinuationCap_AndReportsTruncated()
    {
        // Distinct chunks each round (no repetition), always truncated → only the cap can halt it.
        var chat = new QueuedChatModel(
            Cc("The first distinct part of a long answer keeps going ", "length"),
            Cc("into a second entirely different section with new words ", "length"),
            Cc("and a third unique paragraph that still is not finished ", "length"));

        var result = await Gen(chat, new AlwaysCompleteJudge(), Options(maxContinuations: 2)).GenerateAsync("sys", "go");

        Assert.Equal(3, chat.Calls); // 1 initial + 2 continuations
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task RepeatingContinuation_IsStopped_NotAmplified()
    {
        // The model loops: every continuation repeats the first paragraph. The guard must stop it
        // well before the cap instead of stacking copies into a wall of text.
        const string para = "I'm here to listen and offer any assistance that I can provide today. ";
        var chat = new QueuedChatModel(
            Cc(para, "length"), Cc(para, "length"), Cc(para, "length"),
            Cc(para, "length"), Cc(para, "length"), Cc(para, "length"));

        var result = await Gen(chat, new AlwaysCompleteJudge(), Options(maxContinuations: 6)).GenerateAsync("sys", "hi");

        Assert.Equal(2, chat.Calls); // initial + one repeat detected → stop (not 7)
        // The reply is not a wall: the paragraph appears at most twice, not seven times.
        var occurrences = result.Text.Split("here to listen").Length - 1;
        Assert.True(occurrences <= 2, $"paragraph repeated {occurrences} times");
    }

    [Fact]
    public async Task DoesNotContinue_WhenAutoContinueDisabled()
    {
        var chat = new QueuedChatModel(Cc("partial", "length"));

        var result = await Gen(chat, new AlwaysCompleteJudge(), Options(autoContinue: false)).GenerateAsync("sys", "go");

        Assert.Equal("partial", result.Text);
        Assert.Equal(1, chat.Calls);
    }

    // ---- semantic completion check (natural stop) ----

    [Fact]
    public async Task NaturalStop_ShortReply_NeverConsultsTheJudge()
    {
        var chat = new QueuedChatModel(Cc("Short answer.", "stop"));
        var judge = new ScriptedJudge(false); // would say "continue" if asked

        // Reply (12 chars) is below the min-chars gate, so the judge must not run.
        var result = await Gen(chat, judge, Options(minChars: 500)).GenerateAsync("sys", "hi");

        Assert.Equal("Short answer.", result.Text);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task NaturalStop_LongReply_JudgeSaysContinue_ThenComplete()
    {
        // Both rounds END CLEANLY (terminal punctuation), so the structural check says "looks
        // complete" and the decision falls through to the judge — which is what we're testing.
        var chat = new QueuedChatModel(
            Cc("The story begins and runs for a while but is not done.", "stop"),
            Cc(" And now it is truly finished.", "stop"));
        var judge = new ScriptedJudge(false, true); // not done, then done

        var result = await Gen(chat, judge, Options(minChars: 1)).GenerateAsync("sys", "write a story");

        Assert.Equal("The story begins and runs for a while but is not done. And now it is truly finished.", result.Text);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(2, judge.Calls);
    }

    [Fact]
    public async Task NaturalStop_LongReply_JudgeSaysComplete_StopsImmediately()
    {
        var chat = new QueuedChatModel(Cc("A complete, self-contained answer of some length.", "stop"));
        var judge = new ScriptedJudge(true);

        var result = await Gen(chat, judge, Options(minChars: 1)).GenerateAsync("sys", "explain");

        Assert.Equal(1, chat.Calls);
        Assert.Equal(1, judge.Calls);
        Assert.Equal("A complete, self-contained answer of some length.", result.Text);
    }

    [Fact]
    public async Task NaturalStop_CompletionCheckDisabled_DoesNotJudge()
    {
        var chat = new QueuedChatModel(Cc("A fairly long reply that stopped on its own here.", "stop"));
        var judge = new ScriptedJudge(false);

        // Deliverable request + clean-looking reply, but the judge is disabled → no judge, stop.
        var result = await Gen(chat, judge, Options(completionCheck: false, minChars: 1)).GenerateAsync("sys", "write a report");

        Assert.Equal(1, chat.Calls);
        Assert.Equal(0, judge.Calls);
        Assert.Equal("A fairly long reply that stopped on its own here.", result.Text);
    }

    // ---- deterministic gates: deliverable request + structural incompleteness ----

    [Fact]
    public async Task NaturalStop_NonDeliverableChat_IsNeverContinued_EvenIfJudgeWouldSayContinue()
    {
        // The runaway case: a greeting whose reply stopped naturally. Not a deliverable request, so
        // the judge is never even consulted and it can't be continued.
        var chat = new QueuedChatModel(
            Cc("It's going well, thanks for asking — how are you doing today?", "stop"));
        var judge = new ScriptedJudge(false); // would loop forever if consulted

        var result = await Gen(chat, judge, Options(minChars: 1)).GenerateAsync("sys", "how are things treating you lately?");

        Assert.Equal(1, chat.Calls);
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task NaturalStop_DeliverableThatStopsMidSentence_ContinuesWithoutTheJudge()
    {
        // Ends mid-sentence (no terminal punctuation) → the structural signal continues it, so the
        // judge is never needed. Second round ends cleanly and stops.
        var chat = new QueuedChatModel(
            Cc("Chapter one began on a grey morning and the crew", "stop"),
            Cc(" set out across the harbor at last.", "stop"));
        var judge = new ScriptedJudge(); // must not be consulted

        var result = await Gen(chat, judge, Options(completionCheck: false, minChars: 1)).GenerateAsync("sys", "write me a short story");

        Assert.Equal(2, chat.Calls);
        Assert.Equal(0, judge.Calls);
        Assert.Equal("Chapter one began on a grey morning and the crew set out across the harbor at last.", result.Text);
    }

    // ---- streaming path ----

    [Fact]
    public async Task Streaming_ContinuesAcrossRounds_AndSinkGetsTheWholeReply()
    {
        var chat = new QueuedChatModel(Cc("Hello ", "length"), Cc("world.", "stop"));
        var chunks = new List<string>();
        var sink = new DelegateProgress(chunks.Add);

        var result = await Gen(chat, new AlwaysCompleteJudge(), Options()).GenerateAsync("sys", "go", sink);

        Assert.Equal("Hello world.", result.Text);
        Assert.Equal("Hello world.", string.Concat(chunks));
        Assert.Equal(2, chat.Calls);
    }

    // ---- test doubles ----

    private sealed class QueuedChatModel : IChatModel
    {
        private readonly Queue<ChatCompletion> _results;
        public List<string?> Prefixes { get; } = new();
        public int Calls { get; private set; }

        public QueuedChatModel(params ChatCompletion[] results) => _results = new Queue<ChatCompletion>(results);

        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, bool jsonMode = false,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            Calls++;
            Prefixes.Add(assistantPrefix);
            return Task.FromResult(_results.Dequeue());
        }

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            Calls++;
            Prefixes.Add(assistantPrefix);
            var r = _results.Dequeue();
            sink.Report(r.Text); // one chunk per round is enough to prove streaming across rounds
            return Task.FromResult(r);
        }
    }

    private sealed class ScriptedJudge : ICompletionJudge
    {
        private readonly Queue<bool> _verdicts;
        public int Calls { get; private set; }
        public ScriptedJudge(params bool[] verdicts) => _verdicts = new Queue<bool>(verdicts);

        public Task<bool> IsCompleteAsync(string userRequest, string replySoFar, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_verdicts.Count > 0 ? _verdicts.Dequeue() : true);
        }
    }

    private sealed class DelegateProgress : IProgress<string>
    {
        private readonly Action<string> _on;
        public DelegateProgress(Action<string> on) => _on = on;
        public void Report(string value) => _on(value);
    }
}
