using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The closed set of relations, and the promise that closing it didn't change what already-stored
/// memories mean.
/// </summary>
public class PredicateVocabularyTests
{
    [Fact]
    public void EveryEntryIsDistinctAndWireSafe()
    {
        Assert.Equal(PredicateVocabulary.Names.Count, PredicateVocabulary.Names.Distinct().Count());
        Assert.All(PredicateVocabulary.Names, n =>
        {
            Assert.Equal(n, PredicateVocabulary.Canonical(n));
            Assert.All(n, c => Assert.True(char.IsLower(c) || char.IsDigit(c) || c == '_', $"'{c}' in '{n}'"));
        });
    }

    /// <summary>Without an escape hatch a closed vocabulary silently discards whatever it didn't foresee.</summary>
    [Fact]
    public void ThereIsSomewhereForAnUnforeseenFactToGo()
    {
        Assert.Contains(PredicateVocabulary.Other, PredicateVocabulary.Names);
        Assert.False(PredicateVocabulary.IsSingleValued(PredicateVocabulary.Other));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("lives_in")]
    [InlineData("occupation")]
    [InlineData("diet")]
    public void OneValueAtATime(string predicate)
        => Assert.True(PredicateVocabulary.IsSingleValued(predicate));

    [Theory]
    [InlineData("works_on")]
    [InlineData("dislikes")]
    [InlineData("has_pet")]
    [InlineData("relationship")]
    public void ManyValuesAtOnce(string predicate)
        => Assert.False(PredicateVocabulary.IsSingleValued(predicate));

    [Theory]
    [InlineData("Lives In", "lives_in")]
    [InlineData("lives-in", "lives_in")]
    [InlineData("  WORKS_ON  ", "works_on")]
    [InlineData("drinks coffee black", "drinks_coffee_black")]
    public void SpellingVariantsAreOneSlot(string input, string expected)
        => Assert.Equal(expected, PredicateVocabulary.Canonical(input));

    /// <summary>
    /// Databases written before the vocabulary existed are full of predicates the model invented,
    /// and cardinality has to keep being right about them — an upgrade that changed what a stored
    /// memory means would be the same failure in a new coat.
    /// </summary>
    [Theory]
    [InlineData("job", true)]
    [InlineData("home", true)]
    [InlineData("phone_number", true)]
    [InlineData("drinks_coffee_black", false)]
    [InlineData("buys_materials_for", false)]
    public void LegacyPredicatesStillBehave(string predicate, bool singleValued)
        => Assert.Equal(singleValued, FactSupersession.IsSingleValued(predicate));
}
