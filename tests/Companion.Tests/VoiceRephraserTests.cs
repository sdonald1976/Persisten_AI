using Companion.Core.Abstractions;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The voice rephraser: a deterministic draft goes in; her wording comes out when a model
/// cooperates, and the draft comes back untouched on anything suspicious. It can restyle,
/// never rewrite reality.
/// </summary>
public class VoiceRephraserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private sealed class ThrowingChatModel : IChatModel
    {
        public Task<ChatCompletion> CompleteAsync(string s, string u, bool j = false, string? a = null, CancellationToken ct = default)
            => throw new InvalidOperationException("model down");
        public Task<ChatCompletion> StreamAsync(string s, string u, IProgress<string> sink, string? a = null, CancellationToken ct = default)
            => throw new InvalidOperationException("model down");
    }

    private static LlmVoiceRephraser Build(IServiceProvider sp, IChatModel chat)
        => new(chat, sp.GetRequiredService<IPersonalityService>(),
            sp.GetRequiredService<IProfileStore>(), NullLogger<LlmVoiceRephraser>.Instance);

    [Fact]
    public async Task Passthrough_ReturnsTheDraftVerbatim()
        => Assert.Equal("Good luck today!", await new PassthroughRephraser()
            .RephraseAsync(User, "Good luck today!", "a push"));

    [Fact]
    public async Task AWorkingModel_RestylesTheDraft()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var rephraser = Build(scope.ServiceProvider, new CannedChatModel("Hey you — good luck out there today. I'll be thinking of you!"));

        var text = await rephraser.RephraseAsync(User, "Good luck with your interview today — I'll be thinking of you.", "a push");

        Assert.Contains("good luck out there", text);
    }

    [Fact]
    public async Task AFailedModel_FallsBackToTheDraft()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var rephraser = Build(scope.ServiceProvider, new ThrowingChatModel());

        const string draft = "Good luck with your interview today — I'll be thinking of you.";
        Assert.Equal(draft, await rephraser.RephraseAsync(User, draft, "a push"));
    }

    [Fact]
    public async Task ABallooningRewrite_IsRejected_ForTheDraft()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var rephraser = Build(scope.ServiceProvider, new CannedChatModel(new string('x', 5000)));

        const string draft = "You crossed my mind. How did it go?";
        Assert.Equal(draft, await rephraser.RephraseAsync(User, draft, "a push"));
    }
}
