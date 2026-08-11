using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The prompt catalog: every piece of her language has a key, a description, and a working
/// default; overrides win and clear cleanly; templates fill their placeholders. Wording lives
/// here — control flow never does.
/// </summary>
public class PromptCatalogTests
{
    [Fact]
    public void EveryDefinition_HasKeyDescriptionAndDefault()
    {
        Assert.NotEmpty(Prompts.All);
        Assert.All(Prompts.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Key));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.False(string.IsNullOrWhiteSpace(d.Default));
        });
    }

    [Fact]
    public void Get_ReturnsTheDefault_UntilOverridden_ThenTheOverride_ThenTheDefaultAgain()
    {
        const string key = "thoughts.musing.earlier"; // a quiet key no other test asserts on
        var @default = Prompts.Get(key);
        try
        {
            Prompts.SetOverride(key, "Oh and earlier: {musing}");
            Assert.Equal("Oh and earlier: {musing}", Prompts.Get(key));
            Assert.Equal("Oh and earlier: {musing}", Prompts.OverrideFor(key));
        }
        finally
        {
            Prompts.SetOverride(key, null);
        }
        Assert.Equal(@default, Prompts.Get(key));
        Assert.Null(Prompts.OverrideFor(key));
    }

    [Fact]
    public void Format_FillsPlaceholders_AndLeavesUnknownOnesAlone()
    {
        var text = Prompts.Format("outreach.curiosity", ("question", "How did the interview go?"));
        Assert.Equal("You crossed my mind. How did the interview go?", text);

        // A template whose placeholder wasn't supplied keeps it visible (a loud bug beats a silent one).
        Assert.Contains("{description}", Prompts.Format("outreach.goodluck"));
    }

    [Fact]
    public void UnknownKey_IsRejected()
        => Assert.Throws<KeyNotFoundException>(() => Prompts.SetOverride("no.such.key", "text"));

    [Fact]
    public void TheRenderer_SpeaksFromTheCatalog()
    {
        // Sanity that the biggest consumer actually reads the catalog: its core framing line is
        // the catalog default, so overriding the catalog would change the rendered prompt.
        var packet = new Companion.Core.Domain.ContextPacket { UserMessage = "hi" };
        Assert.Contains(Prompts.Get("renderer.core"), packet.Render());
    }
}
