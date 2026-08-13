using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Where she chooses to be, and why.
///
/// The design's whole justification for a world is continuity rather than simulation, and that
/// stands or falls here: a move she cannot explain is decoration. So every choice carries the
/// reason that produced it, and these pin the reasons as much as the destinations.
///
/// Nothing here knows the name of any room. The policy is given a menu and reads the world's own
/// words for each place — a world with entirely different places works unchanged, which is what
/// keeps the companion from quietly growing a model of somewhere else.
/// </summary>
public class RoamingPolicyTests
{
    private static readonly WorldPlace Study =
        new("study", "the study", "Quiet, with a desk. Where work happens and thinking gets done.");

    private static readonly WorldPlace Garden =
        new("garden", "the garden", "Outside, past the greenhouse. Open sky.");

    private static readonly WorldPlace Greenhouse =
        new("greenhouse", "the greenhouse", "Glass and damp earth. Things grow here, and need attending to.");

    private static readonly WorldPlace Snug =
        new("snug", "the snug", "Warm and still. Somewhere to rest and be calm.");

    private static readonly IReadOnlyList<WorldPlace> Cottage = new[] { Study, Garden, Greenhouse, Snug };

    private static CompanionStateSnapshot State(double spirits = 0, double energy = 0.5, bool rested = false)
        => new() { Spirits = spirits, Energy = energy, Rested = rested };

    private static readonly string[] NothingOnHerMind = Array.Empty<string>();

    [Fact]
    public void WithNoWorld_ThereIsNowhereToGo()
        => Assert.Null(RoamingPolicy.Choose(
            Array.Empty<WorldPlace>(), null, null, State(), NothingOnHerMind));

    [Fact]
    public void WhatSheIsWonderingAbout_DecidesWhereSheGoes()
    {
        // The move a person would explain the same way: she is curious about the greenhouse, so
        // she is in the greenhouse.
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(),
            new[] { "the greenhouse tomatoes — are they ripening yet?" });

        Assert.NotNull(choice);
        Assert.Equal("greenhouse", choice!.PlaceId);
        Assert.Contains("wondering", choice.Reason);
        Assert.Contains("tomatoes", choice.Reason);
    }

    [Fact]
    public void ALowHour_DrawsHerSomewhereRestful()
    {
        var choice = RoamingPolicy.Choose(
            Cottage, "garden", null, State(energy: 0.25), NothingOnHerMind);

        Assert.NotNull(choice);
        Assert.Equal("snug", choice!.PlaceId);
        Assert.Contains("quiet", choice.Reason);
    }

    [Fact]
    public void GoodEnergy_DrawsHerSomewhereWithSomethingToDo()
    {
        var choice = RoamingPolicy.Choose(
            Cottage, "snug", null, State(energy: 0.9), NothingOnHerMind);

        Assert.NotNull(choice);
        Assert.Contains(choice!.PlaceId, new[] { "study", "greenhouse" });
        Assert.Contains("energy", choice.Reason);
    }

    [Fact]
    public void SheDoesNotSetOffForSomewhereSheAlreadyIs()
    {
        var choice = RoamingPolicy.Choose(
            Cottage, "greenhouse", null, State(),
            new[] { "the greenhouse tomatoes" });

        Assert.Null(choice);
    }

    [Fact]
    public void SmallDifferences_DoNotMoveHer()
    {
        // Without a margin she paces: every tiny shift of mood would relocate her, which reads as
        // agitation rather than life. Staying put is the default and moving needs a reason.
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.55), NothingOnHerMind);

        Assert.Null(choice);
    }

    [Fact]
    public void SheDoesNotOscillate_BetweenTwoRooms()
    {
        // From the snug on a high-energy hour she has somewhere to be. Having just come from
        // wherever that is, the same pull should not send her straight back.
        var withoutHistory = RoamingPolicy.Choose(
            Cottage, "snug", null, State(energy: 0.9), NothingOnHerMind);
        var justLeftIt = RoamingPolicy.Choose(
            Cottage, "snug", withoutHistory?.PlaceId, State(energy: 0.9), NothingOnHerMind);

        Assert.NotNull(withoutHistory);
        Assert.NotEqual(withoutHistory!.PlaceId, justLeftIt?.PlaceId);
    }

    [Fact]
    public void EveryChoice_CarriesAReason()
    {
        // A move she cannot explain is decoration, and the vision doc's non-goals rule that out.
        foreach (var energy in new[] { 0.1, 0.5, 0.95 })
        foreach (var spirits in new[] { -0.8, 0.0, 0.8 })
        {
            var choice = RoamingPolicy.Choose(
                Cottage, "study", null, State(spirits, energy),
                new[] { "whether the tomatoes are ripening" });

            if (choice is null)
                continue;

            Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
            Assert.NotEqual("no particular reason", choice.Reason);
        }
    }

    [Fact]
    public void TheSameStateAlwaysGivesTheSameAnswer()
    {
        // Deterministic, so "why are you in the greenhouse?" has one true answer rather than a
        // plausible one — and so this is testable at all.
        var first = RoamingPolicy.Choose(Cottage, "study", null, State(energy: 0.2), NothingOnHerMind);
        var second = RoamingPolicy.Choose(Cottage, "study", null, State(energy: 0.2), NothingOnHerMind);

        Assert.Equal(first?.PlaceId, second?.PlaceId);
        Assert.Equal(first?.Reason, second?.Reason);
    }

    [Fact]
    public void ItWorksInAWorldItHasNeverHeardOf()
    {
        // No room names are baked in anywhere. A completely different world must still work, or
        // the companion has opinions about a layout it is not allowed to know.
        var elsewhere = new[]
        {
            new WorldPlace("bridge", "the bridge", "Instruments and work. Charts to be kept."),
            new WorldPlace("bunk", "the bunk", "Warm and still. Somewhere to rest."),
        };

        var tired = RoamingPolicy.Choose(elsewhere, "bridge", null, State(energy: 0.15), NothingOnHerMind);

        Assert.Equal("bunk", tired?.PlaceId);
    }
}
