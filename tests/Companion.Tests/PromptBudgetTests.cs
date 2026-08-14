using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Models;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The prompt must fit the model's context window, and what gets left out when it doesn't must be
/// our decision rather than the server's.
///
/// The failure these exist to prevent was measured against this project's own configuration, not
/// imagined: a ~7,800-token prompt sent to the configured chat model came back reporting 2,050
/// prompt tokens, the entire system prompt discarded, and the model then denying — fluently, and
/// with no error anywhere — that it had ever been told the thing that was cut. From the outside
/// that is indistinguishable from the companion losing her memory and starting a new conversation,
/// which is exactly how it was reported.
///
/// So these tests pin guarantees, not mechanics. The ranks may be retuned; identity surviving and
/// the conversation outranking the decoration may not.
/// </summary>
public class PromptBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Message Turn(MessageRole role, string text, int minute) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.Empty,
        UserId = "u",
        Role = role,
        Content = text,
        Timestamp = Now.AddMinutes(minute),
    };

    private static ContextItem Memory(string text) =>
        new() { Text = text, Provenance = ContextProvenance.DirectStatement };

    /// <summary>A packet with something in every layer, big enough that not all of it can fit.</summary>
    private static ContextPacket Crowded(int budget) => new()
    {
        UserMessage = "so what do you think?",
        Identities = new PromptIdentityContext
        {
            CompanionName = "Ava",
            CompanionPronouns = "she/her",
            UserName = "Scott",
        },
        Persona = "Warm, direct, a bit dry.",
        RecentMessages = new[]
        {
            Turn(MessageRole.User, "I've been turning over whether to rebuild the deck this summer.", 1),
            Turn(MessageRole.Assistant, "The rot you found last autumn would decide it for me.", 2),
            Turn(MessageRole.User, "Right — and the quote came back higher than I expected.", 3),
        },
        Memories = Enumerable.Range(0, 20)
            .Select(i => Memory($"Scott mentioned something durable and specific, number {i}, worth recalling later."))
            .ToList(),
        MoodNote = "Settled, with a bit of energy to spare.",
        Musing = "I keep coming back to what he said about the garden.",
        CuriosityQuestion = "Does he actually like the deck, or just the idea of it?",
        FamiliarityNote = "Long-standing; shorthand is fine.",
        TemporalNote = "Thursday evening; you last spoke this morning.",
        Diagnostics = Enumerable.Range(0, 30)
            .Select(i => $"DEBUG selection trace entry {i} with padding to make it substantial")
            .ToList(),
        MaxPromptTokens = budget,
    };

    // ---- the guarantee ----

    [Fact]
    public void UnderAnImpossibleBudget_IdentityStillSurvives()
    {
        // The one thing that must never be dropped. A prompt without it isn't a smaller prompt,
        // it's a different entity — and a persistent companion who stops being the same one has
        // failed at the only thing she is for.
        var render = ContextPacketRenderer.Build(Crowded(budget: 1));

        Assert.Contains("Ava", render.Text);
        Assert.Contains("Scott", render.Text);
        Assert.Contains("AUTHORITATIVE IDENTITIES", render.Text);
    }

    [Fact]
    public void UnderAnImpossibleBudget_ThePersonaAndStandingRulesSurvive()
    {
        var render = ContextPacketRenderer.Build(Crowded(budget: 1));

        Assert.Contains("Warm, direct, a bit dry.", render.Text);
    }

    [Fact]
    public void TheConversationOutranksTheDecoration()
    {
        // What a person notices is losing the thread, not losing her mood note. When only some of
        // it fits, the transcript stays and the colour goes.
        var render = ContextPacketRenderer.Build(Crowded(budget: 1200));

        Assert.Contains("the quote came back higher", render.Text);
        Assert.DoesNotContain("recent conversation", render.Dropped);
        Assert.Contains("musing", render.Dropped);
    }

    [Fact]
    public void SqueezedToNothing_TheNewestExchangeStillSurvives()
    {
        // The transcript is guaranteed, not merely ranked first. On a budget the standing rules
        // alone would exhaust, a purely rank-ordered fit dropped the conversation while carefully
        // preserving the instructions telling her to remember it — perfectly herself, with no idea
        // what had just been said.
        var render = ContextPacketRenderer.Build(Crowded(budget: 1));

        Assert.Contains("the quote came back higher", render.Text);
    }

    [Fact]
    public void DebugOutputIsTheFirstThingSacrificed()
    {
        var render = ContextPacketRenderer.Build(Crowded(budget: 700));

        Assert.Contains("diagnostics", render.Dropped);
        Assert.DoesNotContain("DEBUG selection trace entry 0", render.Text);
    }

    [Theory]
    [InlineData(3072)]  // what a default 4096-token window leaves once the reply has its room
    [InlineData(2000)]
    [InlineData(1200)]
    public void TheRenderedPromptActuallyFitsTheBudget(int budget)
    {
        // The point of the whole exercise. Measured the same way the budget is expressed, so the
        // two cannot quietly drift apart. A little slack is allowed for the guaranteed core: it is
        // better to exceed a self-imposed budget by a line than to ship a companion with no
        // identity, and the reply reserve exists to absorb exactly that.
        var render = ContextPacketRenderer.Build(Crowded(budget));
        var actual = ContextAssembler.EstimateTokens(render.Text);

        Assert.True(actual <= budget, $"rendered ~{actual} tokens against a {budget} budget");
    }

    [Fact]
    public void WhatWasLeftOutIsNamed()
    {
        // A degraded prompt that says so can be diagnosed. One that doesn't looks like the model
        // being unreliable, which is where weeks go.
        var render = ContextPacketRenderer.Build(Crowded(budget: 500));

        Assert.True(render.Trimmed);
        Assert.NotEmpty(render.Dropped);
        Assert.All(render.Dropped, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    // ---- not changing what already worked ----

    [Fact]
    public void WithNoBudget_NothingIsDropped()
    {
        var render = ContextPacketRenderer.Build(Crowded(budget: 0));

        Assert.False(render.Trimmed);
        Assert.Contains("DEBUG selection trace entry 0", render.Text);
        Assert.Contains("rebuild the deck", render.Text);
    }

    [Fact]
    public void WithAGenerousBudget_EverythingStillFits()
    {
        var render = ContextPacketRenderer.Build(Crowded(budget: 100_000));

        Assert.Empty(render.Dropped);
    }

    [Fact]
    public void TrimmingDoesNotReorderWhatSurvives()
    {
        // Sections are chosen by rank but emitted in their canonical order. If trimming shuffled
        // the prompt, every surviving section would land somewhere the model has never seen it,
        // which is a different prompt rather than a shorter one.
        var render = ContextPacketRenderer.Build(Crowded(budget: 1500));

        var identity = render.Text.IndexOf("AUTHORITATIVE IDENTITIES", StringComparison.Ordinal);
        var conversation = render.Text.IndexOf("the quote came back higher", StringComparison.Ordinal);

        Assert.True(identity >= 0 && conversation > identity,
            "identity must still come before the transcript");
    }

    // ---- the arithmetic that sets the budget ----

    [Fact]
    public void TheReplyGetsRoomReservedFromTheWindow()
    {
        var endpoint = new EndpointOptions { ContextTokens = 4096, ReplyReserveTokens = 1024 };

        Assert.Equal(3072, endpoint.PromptBudgetTokens);
    }

    [Fact]
    public void ADefaultEndpointAssumesTheSmallCommonWindow()
    {
        // Ollama serves 4096 unless told otherwise, regardless of what the model was trained for
        // — verified directly, and it silently ignores num_ctx sent over the OpenAI-compatible
        // endpoint. Guessing high here costs identity; guessing low costs a little nuance.
        Assert.Equal(4096, new EndpointOptions().ContextTokens);
    }

    [Fact]
    public void AnAbsurdReserveCannotStarveThePrompt()
    {
        var endpoint = new EndpointOptions { ContextTokens = 2048, ReplyReserveTokens = 999_999 };

        Assert.True(endpoint.PromptBudgetTokens >= 512);
    }
}
