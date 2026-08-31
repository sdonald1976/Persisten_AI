using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Characterization of the turn's DECISION SEQUENCE — which stages run, in what order.
///
/// <c>CompleteTurnAsync</c> is over a thousand lines and its ordering constraints are
/// enforced only by statement position: the reply gate runs before storage so a refused
/// reply never becomes the next turn's context; privacy classification runs before anything
/// derived. Nothing currently fails if an extraction reorders them.
///
/// This pins the ORDER and the SET of stages, not their verdicts. Verdicts depend on the
/// model and on retrieval; order is a property of the pipeline itself, and order is what the
/// extraction phases put at risk.
/// </summary>
public class TurnSequenceCharacterizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<List<string>> StagesForAsync(
        string message, bool shadowEnabled = false)
    {
        await using var host = new TestHost(
            Now,
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = shadowEnabled ? "true" : "false",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
                ["Companion:RendererShadow:TimeoutSeconds"] = "2",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(CompanionSeeder.DemoUserId, conversationId, message);

        using var read = host.CreateScope();
        var turn = (await read.ServiceProvider.GetRequiredService<IDiagnosticsStore>()
            .GetRecentTurnsAsync(CompanionSeeder.DemoUserId, 1)).Single();

        return (turn.Decisions ?? "")
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('=')[0])
            .ToList();
    }

    [Fact]
    public async Task AnOrdinaryTurn_RunsItsStagesInAStableOrder()
    {
        var first = await StagesForAsync("What did we decide about the shed?");
        var again = await StagesForAsync("What did we decide about the shed?");

        // Determinism first: a characterization test that is itself unstable is worthless.
        Assert.Equal(first, again);
        Assert.NotEmpty(first);
    }

    [Fact]
    public async Task PrivacyClassification_PrecedesEverythingDerived()
    {
        var stages = await StagesForAsync("What did we decide about the shed?");

        var privacy = stages.IndexOf("privacy");
        Assert.True(privacy >= 0, $"no privacy stage in [{string.Join(", ", stages)}]");

        // Everything that could derive or record something must come after the decision
        // about whether this turn may be derived from at all.
        foreach (var derived in new[] { "memory.derived", "tools", "plan.frame", "renderer.shadow", "extraction" })
        {
            var at = stages.IndexOf(derived);
            if (at >= 0)
                Assert.True(at > privacy,
                    $"'{derived}' runs before privacy classification: [{string.Join(", ", stages)}]");
        }
    }

    [Fact]
    public async Task TheFrameStage_FollowsTools_AndPrecedesTheNativeAssembly()
    {
        // The R-02 move preserved this ordering exactly; this is what proves it stays.
        var stages = await StagesForAsync(
            "Let's roleplay: you're a lighthouse keeper and I'm a sailor.", shadowEnabled: true);

        var tools = stages.IndexOf("tools");
        var frame = stages.IndexOf("plan.frame");
        var assembly = stages.IndexOf("plan.native-v3.tools");

        Assert.True(frame >= 0, $"no frame stage in [{string.Join(", ", stages)}]");
        Assert.True(tools >= 0 && tools < frame,
            $"tools must precede the frame: [{string.Join(", ", stages)}]");
        if (assembly >= 0)
            Assert.True(frame < assembly,
                $"the frame must precede the native assembly: [{string.Join(", ", stages)}]");
    }

    [Fact]
    public async Task TheFrameStageRuns_WhetherOrNotAnyoneIsObserving()
    {
        var observed = await StagesForAsync(
            "Let's roleplay: you're a lighthouse keeper and I'm a sailor.", shadowEnabled: true);
        var unobserved = await StagesForAsync(
            "Let's roleplay: you're a lighthouse keeper and I'm a sailor.", shadowEnabled: false);

        Assert.Contains("plan.frame", observed);
        Assert.Contains("plan.frame", unobserved);

        // The shadow-only stages are the ONLY difference the flag makes to the sequence.
        var shadowOnly = new[] { "plan.native-v3", "plan.native-v3.tools", "renderer.shadow" };
        Assert.Equal(
            observed.Where(s => !shadowOnly.Contains(s)),
            unobserved.Where(s => !shadowOnly.Contains(s)));
    }

    [Fact]
    public async Task TheStageSet_IsPinnedForAnOrdinaryTurn()
    {
        var stages = await StagesForAsync("What did we decide about the shed?");

        // Named explicitly so that ADDING a stage to the turn is a deliberate, reviewed
        // change rather than something that appears silently during an extraction.
        var expected = new[]
        {
            // Observed, not guessed. Adding to this list should be a deliberate edit that
            // says a new stage was intended.
            "privacy", "roleplay", "memory.derived", "project", "interpretation",
            "curiosity", "register", "intent", "packet.budget", "plan", "tools",
            "plan.frame", "plan.native-v3", "plan.native-v3.tools", "plan.promotion",
            "reply.gate", "plan.fidelity", "renderer.canary", "renderer.shadow",
            "extraction",

            // Deliberate (Stheno-free work): the per-turn model-call ledger, recorded on
            // every turn so "which models did this turn call" is a stated fact. The route's
            // own stages (route.stheno-free, plan.executive) appear only on routed turns and
            // are pinned by SthenoFreeRouteTests, not here.
            "models.called",
        };

        var unexpected = stages.Distinct().Except(expected).ToList();
        Assert.True(unexpected.Count == 0,
            "new turn stages appeared that this characterization does not know about: "
            + string.Join(", ", unexpected)
            + ". Add them here deliberately if they are intended.");
    }
}
