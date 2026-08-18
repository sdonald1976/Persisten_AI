using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The seam a learned roaming policy would plug into, and the guarantees that make it worth having.
///
/// Nothing here trains anything, and there is no second policy. The point of building the seam
/// first is that the migration this project uses everywhere else needs two implementations running
/// on one observation before either is trusted, and a static method with seven positional
/// parameters is not something a second implementation can be written against.
///
/// The behaviour of the rule itself is pinned by <see cref="RoamingPolicyTests"/> and
/// <see cref="RoamingRestlessnessTests"/>, which still call the original entry point and were not
/// touched. That is deliberate: a refactor that needs its own tests rewritten has not been shown
/// to preserve anything.
/// </summary>
public class RoamingPolicySeamTests
{
    private static readonly WorldPlace Study =
        new("study", "the study", "Quiet, with a desk. Where work happens and thinking gets done.");

    private static readonly WorldPlace Garden =
        new("garden", "the garden", "Outside, past the greenhouse. Open sky.");

    private static readonly WorldPlace Greenhouse =
        new("greenhouse", "the greenhouse", "Glass and damp earth. Things grow here, and need attending to.");

    private static readonly IReadOnlyList<WorldPlace> Cottage = new[] { Study, Garden, Greenhouse };

    private static CompanionStateSnapshot State(double spirits = 0, double energy = 0.5)
        => new() { Spirits = spirits, Energy = energy };

    private static RoamingObservation Watching(
        string? current = "study", IReadOnlyList<string>? onHerMind = null, TimeSpan? here = null)
        => new(Cottage, current, null, State(), onHerMind ?? Array.Empty<string>(), here);

    /// <summary>
    /// The refactor's actual obligation: the interface and the original call must produce the same
    /// decision from the same situation. Two code paths that can disagree are two policies, and
    /// only one of them would be the one under test.
    /// </summary>
    [Theory]
    [InlineData("study")]
    [InlineData("garden")]
    [InlineData(null)]
    public void TheInterfaceAndTheOriginalCall_Agree(string? current)
    {
        var onHerMind = new[] { "the greenhouse tomatoes" };

        var direct = RoamingPolicy.Choose(
            Cottage, current, null, State(), onHerMind, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(45));
        var seam = new HeuristicRoamingPolicy(TimeSpan.FromMinutes(45))
            .Deliberate(Watching(current, onHerMind, TimeSpan.FromMinutes(10)));

        Assert.Equal(direct?.PlaceId, seam.Move?.PlaceId);
        Assert.Equal(direct?.Reason, seam.Move?.Reason);
    }

    /// <summary>
    /// Everything considered, not only the winner. The losers are what makes two policies
    /// comparable at all: the same top pick from a completely different ranking is a coincidence,
    /// not agreement, and only the ranking can tell those apart.
    /// </summary>
    [Fact]
    public void EveryPlaceIsScored_AndTheRankingIsStable()
    {
        var first = new HeuristicRoamingPolicy().Deliberate(Watching());
        var again = new HeuristicRoamingPolicy().Deliberate(Watching());

        Assert.Equal(Cottage.Count, first.Ranked.Count);
        Assert.Equal(
            first.Ranked.Select(r => r.PlaceId),
            again.Ranked.Select(r => r.PlaceId));
        Assert.Equal(
            first.Ranked.Select(r => r.Score).OrderByDescending(s => s),
            first.Ranked.Select(r => r.Score));
    }

    /// <summary>
    /// Staying is a decision with a reason, not the absence of one. "Why is she still in the
    /// study?" is asked at least as often as "why did she move", and before this it was the one
    /// outcome that left no record.
    /// </summary>
    [Fact]
    public void StayingCarriesItsOwnReason()
    {
        var settled = new HeuristicRoamingPolicy().Deliberate(Watching(current: "study"));

        Assert.Null(settled.Move);
        Assert.False(string.IsNullOrWhiteSpace(settled.Reason));
        Assert.NotNull(settled.Best);
    }

    [Fact]
    public void WithNoWorld_ThereIsNothingToRankAndItSaysWhy()
    {
        var nowhere = new HeuristicRoamingPolicy().Deliberate(
            new RoamingObservation(Array.Empty<WorldPlace>(), null, null, State(), Array.Empty<string>()));

        Assert.Null(nowhere.Move);
        Assert.Empty(nowhere.Ranked);
        Assert.Contains("no world", nowhere.Reason);
    }

    /// <summary>
    /// The margin and the threshold are both on the record. They are the first two numbers anyone
    /// looks at when a policy seems too restless or too inert, and deriving them afterwards from a
    /// score list means recomputing the rule to explain the rule.
    /// </summary>
    [Fact]
    public void TheMarginAndTheThresholdItHadToBeatAreBothRecorded()
    {
        var justArrived = new HeuristicRoamingPolicy(TimeSpan.FromMinutes(45))
            .Deliberate(Watching(here: TimeSpan.Zero));
        var longSettled = new HeuristicRoamingPolicy(TimeSpan.FromMinutes(45))
            .Deliberate(Watching(here: TimeSpan.FromHours(2)));

        // Getting up is hard at first and easier the longer she has been sitting.
        Assert.Equal(RoamingPolicy.MoveThreshold, justArrived.MoveThreshold, 3);
        Assert.True(longSettled.MoveThreshold < justArrived.MoveThreshold);
        Assert.True(justArrived.Margin >= 0);
    }

    /// <summary>The wiring: the heuristic is what runs unless something else is registered.</summary>
    [Fact]
    public async Task TheHeuristicIsTheRegisteredDefault()
    {
        await using var host = new TestHost(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var scope = host.CreateScope();

        Assert.Equal("heuristic", scope.ServiceProvider.GetRequiredService<IRoamingPolicy>().Name);
    }

    /// <summary>
    /// And it can be replaced, which is the only thing the seam is for. A policy registered over
    /// the default is the one that decides — no static call reaches around it.
    /// </summary>
    [Fact]
    public async Task ARegisteredPolicyReplacesIt()
    {
        await using var host = new TestHost(
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            configureServices: s => s.AddSingleton<IRoamingPolicy>(new AlwaysGarden()));
        using var scope = host.CreateScope();

        var policy = scope.ServiceProvider.GetRequiredService<IRoamingPolicy>();
        Assert.Equal("always-garden", policy.Name);
        Assert.Equal("garden", policy.Deliberate(Watching()).Move?.PlaceId);
    }

    private sealed class AlwaysGarden : IRoamingPolicy
    {
        public string Name => "always-garden";

        public RoamingDeliberation Deliberate(RoamingObservation observation)
        {
            var garden = new RoamingChoice("garden", "it always picks the garden", 1.0);
            return new RoamingDeliberation(new[] { garden }, garden, garden.Reason, 0);
        }
    }
}
