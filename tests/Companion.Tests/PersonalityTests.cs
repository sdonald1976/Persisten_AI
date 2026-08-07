using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The configurable personality: a default preset from config, per-user preset selection, free-text
/// tweaks layered on top, and natural-language switching — so the companion isn't stuck dry.
/// </summary>
public class PersonalityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private static PersonalityService Service(string defaultPreset = "warm", IdentityOptions? identity = null) =>
        new(Options.Create(new PersonalityOptions { Default = defaultPreset }),
            Options.Create(identity ?? new IdentityOptions()));

    // ---- resolution + composition ----

    [Fact]
    public void EmptyProfile_UsesTheConfiguredDefault()
    {
        var active = Service("witty").Active(new UserProfile { UserId = User });
        Assert.Equal("witty", active.Name);
    }

    [Fact]
    public void ProfileChoice_WinsOverTheDefault()
    {
        var active = Service("warm").Active(new UserProfile { UserId = User, PersonalityPreset = "direct" });
        Assert.Equal("direct", active.Name);
    }

    [Fact]
    public void UnknownPreset_FallsBackToTheDefault()
    {
        var active = Service("playful").Active(new UserProfile { UserId = User, PersonalityPreset = "nonsense" });
        Assert.Equal("playful", active.Name);
    }

    [Fact]
    public void Compose_LayersCustomTweaksOnTopOfThePreset()
    {
        var profile = new UserProfile { UserId = User, PersonalityPreset = "witty", Persona = "Keep replies under 3 sentences." };
        var text = Service().Compose(profile);

        Assert.Contains("irreverent", text);                       // the preset voice
        Assert.Contains("Keep replies under 3 sentences.", text);  // the user's tweak, layered on
    }

    [Theory]
    [InlineData("witty")]
    [InlineData("Witty & irreverent")]
    [InlineData("WITTY")]
    public void Find_MatchesByKeyOrLabel_CaseInsensitive(string query)
        => Assert.Equal("witty", Service().Find(query)!.Name);

    // ---- natural-language switching ----

    [Theory]
    [InlineData("switch to the witty personality", "witty")]
    [InlineData("set personality to direct", "direct")]
    [InlineData("use the playful personality", "playful")]
    [InlineData("change your personality to sage", "sage")]
    public void Parser_RecognizesPersonalitySwitch(string text, string expected)
    {
        var intent = new RuleBasedIntentParser().Parse(text);
        Assert.Equal(IntentKind.SetPersonality, intent.Kind);
        Assert.Equal(expected, intent.Argument);
    }

    [Fact]
    public void Parser_TreatsAskingForOptions_AsPersonalityWithNoChoice()
    {
        var intent = new RuleBasedIntentParser().Parse("what personalities are there?");
        Assert.Equal(IntentKind.SetPersonality, intent.Kind);
        Assert.Null(intent.Argument);
    }

    [Fact]
    public void Parser_DoesNotConfuse_StyleTweaks_OrFreeTextPersona()
    {
        Assert.Equal(IntentKind.AdjustStyle, new RuleBasedIntentParser().Parse("be more concise").Kind);
        Assert.Equal(IntentKind.SetPersona, new RuleBasedIntentParser().Parse("set persona to a grumpy pirate").Kind);
    }

    // ---- end to end through the brain ----

    [Fact]
    public async Task DefaultPersonality_ReachesTheSystemPrompt()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "what should we work on today?");

        Assert.Contains("genuinely curious", trace.Packet.Render()); // the default "warm" voice is present
    }

    [Fact]
    public async Task SwitchingPersonality_Persists_AndChangesTheSystemPrompt()
    {
        await using var host = new TestHost(Now);
        Guid conv;
        using (var scope = host.CreateScope())
            conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
        {
            var reply = await scope.ServiceProvider.GetRequiredService<IAgent>()
                .HandleAsync(User, conv, "switch to the direct personality");
            Assert.Equal(IntentKind.SetPersonality, reply.Intent);
            Assert.Contains("direct", reply.Text, StringComparison.OrdinalIgnoreCase);
        }

        using (var verify = host.CreateScope())
        {
            var profile = await verify.ServiceProvider.GetRequiredService<IProfileStore>().GetOrCreateAsync(User);
            Assert.Equal("direct", profile.PersonalityPreset);

            var trace = await verify.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conv, "what should we work on today?");
            Assert.Contains("concise and precise", trace.Packet.Render()); // now the "direct" voice
        }
    }

    [Fact]
    public async Task UnknownPersonality_ListsTheOptions()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var reply = await scope.ServiceProvider.GetRequiredService<IAgent>()
            .HandleAsync(User, conv, "set personality to grumpy-badger");

        Assert.Equal(IntentKind.SetPersonality, reply.Intent);
        Assert.Contains("warm", reply.Text);   // the menu of real presets
        Assert.Contains("witty", reply.Text);
    }

    // ---- identity (name / gender / pronouns) ----

    [Fact]
    public void Identity_DefaultsFromConfig()
    {
        var id = Service(identity: new IdentityOptions { Name = "Ava", Gender = "female", Pronouns = "she/her" })
            .Identity(new UserProfile { UserId = User });
        Assert.Equal("Ava", id.Name);
        Assert.Equal("she/her", id.Pronouns);
    }

    [Fact]
    public void Identity_ProfileOverridesConfig()
    {
        var id = Service().Identity(new UserProfile { UserId = User, CompanionName = "Nova", CompanionPronouns = "they/them" });
        Assert.Equal("Nova", id.Name);
        Assert.Equal("they/them", id.Pronouns);
    }

    [Fact]
    public void Compose_LeadsWithIdentity()
    {
        var text = Service(identity: new IdentityOptions { Name = "Ava", Gender = "female", Pronouns = "she/her" })
            .Compose(new UserProfile { UserId = User });
        Assert.Contains("Your name is Ava", text);
        Assert.Contains("a woman", text);
        Assert.Contains("she/her", text);
    }

    [Theory]
    [InlineData("your name is Ava")]
    [InlineData("you're a woman")]
    [InlineData("call yourself Nova")]
    [InlineData("use she/her")]
    public void Parser_RecognizesIdentityDirectives(string text)
        => Assert.Equal(IntentKind.SetIdentity, new RuleBasedIntentParser().Parse(text).Kind);

    [Fact]
    public async Task SettingGender_PersistsAndSetsPronouns_AndReachesThePrompt()
    {
        await using var host = new TestHost(Now);
        Guid conv;
        using (var scope = host.CreateScope())
            conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(User, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
        {
            var reply = await scope.ServiceProvider.GetRequiredService<IAgent>()
                .HandleAsync(User, conv, "your name is Nova and you're a woman");
            Assert.Equal(IntentKind.SetIdentity, reply.Intent);
        }

        using (var verify = host.CreateScope())
        {
            var profile = await verify.ServiceProvider.GetRequiredService<IProfileStore>().GetOrCreateAsync(User);
            Assert.Equal("Nova", profile.CompanionName);
            Assert.Equal("female", profile.CompanionGender);
            Assert.Equal("she/her", profile.CompanionPronouns);

            var trace = await verify.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(User, conv, "what should we work on today?");
            Assert.Contains("Nova", trace.Packet.Render());
            Assert.Contains("she/her", trace.Packet.Render());
        }
    }

    [Fact]
    public async Task DefaultIdentity_IsFemale_AndReachesThePrompt()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(User, conv, "what should we work on today?");

        // Default identity (Ava, she/her) is present out of the box.
        Assert.Contains("Ava", trace.Packet.Render());
        Assert.Contains("she/her", trace.Packet.Render());
    }
}
