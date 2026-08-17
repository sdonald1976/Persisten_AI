using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Whether a new fact replaces an old one or joins it.
///
/// Both directions of this were wrong in one real conversation, and the reason neither was caught
/// before is that the unit suite constructed candidates with matching predicates while the live
/// extraction model invents them. Starting a second project put both in the slot
/// <c>user/works_on</c> and deleted the first; changing a preference produced the predicates
/// <c>drinks_coffee_black</c> and <c>prefers</c>, which never met.
/// </summary>
public class FactSupersessionTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("lives_in")]
    [InlineData("birthday")]
    [InlineData("job")]
    [InlineData("pronouns")]
    [InlineData("diet")]
    [InlineData("Lives In")]   // spacing and case are not part of the identity
    [InlineData("lives-in")]
    public void OneValueAtATime(string predicate)
        => Assert.True(FactSupersession.IsSingleValued(predicate));

    /// <summary>
    /// The predicates that caused the damage. A person has any number of projects, dislikes and
    /// pets, so a new one is never on its own a reason to drop an old one.
    /// </summary>
    [Theory]
    [InlineData("works_on")]
    [InlineData("dislikes")]
    [InlineData("likes")]
    [InlineData("prefers")]
    [InlineData("has_pet")]
    [InlineData("drinks_coffee_black")]  // the extractor bakes the value into the predicate
    [InlineData("fact")]
    [InlineData(null)]
    [InlineData("")]
    public void ManyValuesAtOnce(string? predicate)
        => Assert.False(FactSupersession.IsSingleValued(predicate));

    [Theory]
    [InlineData("Actually I've gone off black coffee. I take oat milk lattes now.")]
    [InlineData("I no longer work at the university.")]
    [InlineData("I've switched to decaf.")]
    [InlineData("I used to live in Norwich.")]
    [InlineData("Scratch that, it's on Tuesday.")]
    [InlineData("I don't do keto any more.")]
    [InlineData("These days I mostly cycle.")]
    [InlineData("I gave up the allotment.")]
    public void ChangingSomething_IsSignalled(string message)
        => Assert.True(FactSupersession.SignalsReplacement(new[] { message }));

    /// <summary>
    /// Adding a thing. None of these may ever be read as a replacement — the first is the exact
    /// sentence that cost a real user their first project.
    /// </summary>
    [Theory]
    [InlineData("I've started a second thing too - a raised-bed build over at the Marsh Lane plot.")]
    [InlineData("Also I don't like coriander. Never have.")]
    [InlineData("I'm rebuilding the greenhouse irrigation before the frost.")]
    [InlineData("Separate thing entirely: I'm writing a talk on soil chemistry.")]
    [InlineData("I have a corgi called Kanga.")]
    public void AddingSomething_IsNotSignalled(string message)
        => Assert.False(FactSupersession.SignalsReplacement(new[] { message }));

    [Fact]
    public void AnySourceCarryingTheSignalCounts()
    {
        // The excerpt is the precise source; the turn's messages are the forgiving one.
        Assert.True(FactSupersession.SignalsReplacement(
            new[] { "I take oat milk lattes now." },
            new[] { "Actually I've gone off black coffee. I take oat milk lattes now." }));

        Assert.False(FactSupersession.SignalsReplacement(
            new[] { "a raised-bed build" }, new[] { "I've started a second thing too." }));

        Assert.False(FactSupersession.SignalsReplacement(null, Array.Empty<string>()));
    }
}
