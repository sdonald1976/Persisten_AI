using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The familiarity dial — month three sounds different from day one, and closeness is earned on
/// BOTH axes (tenure and real talk), never faked from one alone.
/// </summary>
public class FamiliarityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    [Theory]
    [InlineData(0, 5, FamiliarityStage.New)]        // just met
    [InlineData(10, 60, FamiliarityStage.Acquainted)]
    [InlineData(40, 200, FamiliarityStage.Familiar)]
    [InlineData(120, 500, FamiliarityStage.Close)]
    [InlineData(120, 10, FamiliarityStage.New)]      // months known but barely talked → not close
    [InlineData(1, 500, FamiliarityStage.New)]       // a frantic first day → still new
    [InlineData(30, 500, FamiliarityStage.Familiar)] // depth is there, tenure caps it
    public void TheStage_IsTheLowerOfTenureAndDepth(double days, int messages, FamiliarityStage expected)
        => Assert.Equal(expected, FamiliarityTracker.ComputeStage(days, messages));

    [Fact]
    public void ANewRelationship_SaysSo_WithoutPresumingCloseness()
    {
        var note = new FamiliaritySnapshot { DaysKnown = 0.5, UserMessages = 3, Stage = FamiliarityStage.New }
            .Describe();
        Assert.Contains("only just met", note);
    }

    [Fact]
    public void ACloseRelationship_UnlocksTeasingAndDirectness()
    {
        var note = new FamiliaritySnapshot { DaysKnown = 200, UserMessages = 900, Stage = FamiliarityStage.Close }
            .Describe();
        Assert.Contains("in-jokes", note);
        Assert.Contains("known each other for", note); // grounded in real tenure
    }

    [Fact]
    public async Task TheTurnsPrompt_CarriesTheCalibration()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var conv = (await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test")).Id;

        var trace = await sp.GetRequiredService<ICompanion>().RespondAsync(User, conv, "hey there");
        var rendered = trace.Packet.Render();

        Assert.Contains("Where the relationship is", rendered);
        // A brand-new user (this is their first message) must land on the cautious end.
        Assert.Contains("only just met", rendered);
    }

    [Fact]
    public async Task History_MovesTheDialForward()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IConversationStore>();
        var conv = await store.StartConversationAsync(User, "t", "mock", "test");

        // A month of real talk: first message 30 days ago, 30 messages along the way.
        for (var i = 0; i < 30; i++)
        {
            await store.AddMessageAsync(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conv.Id, UserId = User,
                Role = MessageRole.User, Content = $"hello again ({i})", Timestamp = Now.AddDays(-30).AddDays(i),
            });
        }

        var snapshot = await sp.GetRequiredService<IFamiliarityTracker>().BuildAsync(User);

        Assert.Equal(FamiliarityStage.Acquainted, snapshot.Stage); // 30 days but only ~30 messages
        Assert.Contains("talking for a little while", snapshot.Describe());
    }
}
