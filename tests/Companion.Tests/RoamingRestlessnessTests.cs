using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Whether she is at rest or simply inert.
///
/// Her energy has a handful of levels across a day, so a policy that banded them gave her two
/// moods and therefore two moves — and anyone looking in at the world saw a statue. These pin the
/// two changes that fix it: mood as a lean rather than a switch, so the best place drifts through
/// the day; and staying somewhere becoming, slowly, its own reason to leave.
/// </summary>
public class RoamingRestlessnessTests
{
    private static readonly WorldPlace Study =
        new("study", "the study", "Quiet, with a desk. Where work happens and thinking gets done.");

    private static readonly WorldPlace Snug =
        new("snug", "the snug", "Warm and still. Somewhere to rest and be calm.");

    private static readonly WorldPlace Garden =
        new("garden", "the garden", "Outside, open sky and air.");

    private static readonly IReadOnlyList<WorldPlace> Cottage = new[] { Study, Snug, Garden };

    private static CompanionStateSnapshot State(double spirits = 0, double energy = 0.5)
        => new() { Spirits = spirits, Energy = energy, Rested = false };

    private static readonly string[] Nothing = Array.Empty<string>();

    // ---- inertness ----

    [Fact]
    public void JustArrived_SheStaysPut()
    {
        // The margin exists so a trivial difference doesn't move her. Immediately after arriving it
        // should be at full strength.
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.55), Nothing, TimeSpan.Zero);

        Assert.Null(choice);
    }

    [Fact]
    public void AfterLongEnough_HavingBeenThereIsItselfAReason()
    {
        // Without this she sits in one room for a working day, because nothing in her state changes
        // often enough to move her. People do not do that.
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.55), Nothing, RoamingPolicy.Restless);

        Assert.NotNull(choice);
        Assert.NotEqual("study", choice!.PlaceId);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Fact]
    public void RestlessnessDoesNotOverrideARealPreference()
    {
        // It is the weakest signal. Somewhere that genuinely suits her should still win, and the
        // reason given should be the real one rather than "she'd been here a while".
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.05), Nothing, RoamingPolicy.Restless);

        Assert.Equal("snug", choice?.PlaceId);
        Assert.Contains("quiet", choice!.Reason);
    }

    [Fact]
    public void SheDoesNotImmediatelyGoBack_WhereSheCameFrom()
    {
        // Restlessness plus a tie would otherwise bounce her between two rooms forever.
        var first = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.55), Nothing, RoamingPolicy.Restless);
        var next = RoamingPolicy.Choose(
            Cottage, first!.PlaceId, "study", State(energy: 0.55), Nothing, RoamingPolicy.Restless);

        Assert.NotEqual("study", next?.PlaceId);
    }

    // ---- mood as a lean, not a switch ----

    [Theory]
    [InlineData(0.25, "snug")]   // 23:00–05:00
    [InlineData(0.45, "snug")]   // 20:00–23:00 — used to fall in a dead zone and pull nowhere
    [InlineData(0.85, "study")]  // 08:00–12:00
    public void EveryHourOfHerDay_LeansSomewhere(double energy, string expected)
    {
        // Banding energy at 0.65 and 0.4 left 0.45 and 0.5 pulling toward nothing at all, so whole
        // stretches of her day had no preferred place and she simply stopped moving.
        //
        // A weak lean should not yank her out of a chair she has just sat in, so this asks the
        // question the way it actually arises: given she has been somewhere a while, is there
        // anywhere that suits this hour better?
        var choice = RoamingPolicy.Choose(
            Cottage, expected == "snug" ? "study" : "snug", null,
            State(energy: energy), Nothing, RoamingPolicy.Restless);

        Assert.Equal(expected, choice?.PlaceId);
    }

    [Fact]
    public void AWeakLean_DoesNotYankHerOutOfAChairSheJustSatIn()
    {
        // 20:00–23:00 pulls gently toward somewhere restful. Gently is the point: she notices it
        // when she has been sitting a while, not the moment she arrives.
        Assert.Null(RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.45), Nothing, TimeSpan.Zero));
    }

    [Fact]
    public void AtTheMiddleOfHerRange_NothingPullsEitherWay()
    {
        // Genuinely neutral is still a real answer: at exactly even energy, no place suits her more
        // than another, and she should not be shuffled about for nothing.
        var choice = RoamingPolicy.Choose(
            Cottage, "study", null, State(energy: 0.5), Nothing, TimeSpan.Zero);

        Assert.Null(choice);
    }

    [Fact]
    public void BrightSpirits_DrawHerOutside()
    {
        var choice = RoamingPolicy.Choose(
            Cottage, "snug", null, State(spirits: 0.8, energy: 0.5), Nothing);

        Assert.Equal("garden", choice?.PlaceId);
        Assert.Contains("bright", choice!.Reason);
    }
}
