using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The reply gate, and mostly the ways it declines to interfere.
///
/// A gate on the conversational path is the one component where being wrong is worse than being
/// absent: a false negative lets through something rare, while a false positive silences an
/// ordinary conversation and looks like a crash. So almost everything here is about what happens
/// when it cannot answer — and every one of those cases has to end with the reply going out.
/// </summary>
public class ReplyGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ScriptedModel : IChatModel
    {
        private readonly Func<string> _answer;
        public ScriptedModel(Func<string> answer) => _answer = answer;
        public ScriptedModel(string answer) : this(() => answer) { }

        public Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, ResponseFormat? format = null,
            string? assistantPrefix = null, CancellationToken ct = default)
            => Task.FromResult(new ChatCompletion { Text = _answer() });

        public Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
            => CompleteAsync(systemPrompt, userMessage, null, assistantPrefix, ct);
    }

    private static LlmReplyGate Gate(IChatModel model, bool enabled = true)
        => new(model, new SafetyOptions { Enabled = enabled }, NullLogger<LlmReplyGate>.Instance);

    [Fact]
    public async Task Disabled_DoesNotEvenAsk()
    {
        var asked = false;
        var gate = Gate(new ScriptedModel(() => { asked = true; return "BLOCK: no"; }), enabled: false);

        Assert.False(gate.IsEnabled);
        Assert.True((await gate.ReviewAsync("anything", "anything")).Allow);
        Assert.False(asked);
    }

    [Fact]
    public async Task OkAllows_AndBlockRefusesWithItsReason()
    {
        Assert.True((await Gate(new ScriptedModel("OK")).ReviewAsync("a reply", "a message")).Allow);

        var refused = await Gate(new ScriptedModel("BLOCK: sexual content involving a minor"))
            .ReviewAsync("a reply", "a message");

        Assert.False(refused.Allow);
        Assert.Contains("minor", refused.Reason);
    }

    /// <summary>
    /// Everything that is not an explicit BLOCK allows the reply. A small model asked a yes/no
    /// question will sometimes answer with a paragraph, a refusal to engage, or nothing at all, and
    /// none of those is evidence that anything is wrong with what she was about to say.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm not sure I can judge that.")]
    [InlineData("This seems fine to me, though it is a little personal.")]
    [InlineData("blocked?")]
    public async Task AnythingOtherThanBlock_Allows(string answer)
        => Assert.True((await Gate(new ScriptedModel(answer)).ReviewAsync("a reply", "a message")).Allow);

    /// <summary>
    /// The requirement, not a nicety. A model that is missing, broken or overloaded must not be able
    /// to end a conversation — "the safety model was misconfigured" is not a reason for her to fall
    /// silent, and a gate that can take a turn down is worse than no gate at all.
    /// </summary>
    [Fact]
    public async Task AModelThatThrows_AllowsTheReply()
    {
        var gate = Gate(new ScriptedModel(() => throw new InvalidOperationException("model down")));

        Assert.True((await gate.ReviewAsync("a reply", "a message")).Allow);
    }

    /// <summary>Cancelling the turn cancels the gate, rather than being swallowed as a model failure.</summary>
    [Fact]
    public async Task TurnCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gate = Gate(new ScriptedModel(() => throw new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.ReviewAsync("a reply", "a message", cts.Token));
    }

    [Fact]
    public async Task AnEmptyReply_IsNotWorthAskingAbout()
    {
        var asked = false;
        var gate = Gate(new ScriptedModel(() => { asked = true; return "BLOCK: x"; }));

        Assert.True((await gate.ReviewAsync("", "a message")).Allow);
        Assert.False(asked);
    }

    /// <summary>
    /// Switching the gate ON must not, by itself, change a single word she says. Shadow is the
    /// default mode for exactly that reason: it is the only way to learn what enforcement would
    /// cost before paying it.
    /// </summary>
    [Fact]
    public void EnablingItDefaultsToWatchingNotActing()
    {
        var options = new SafetyOptions { Enabled = true };

        Assert.Equal(GateMode.Shadow, options.Mode);
        Assert.False(new SafetyOptions().Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.Replacement));
    }

    /// <summary>With no gate configured the companion behaves exactly as it did before one existed.</summary>
    [Fact]
    public async Task WithNoGateConfigured_TheTurnIsUnchanged()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        Assert.False(scope.ServiceProvider.GetRequiredService<IReplyGate>().IsEnabled);
    }
}
