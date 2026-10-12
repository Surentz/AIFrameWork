using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class SpellingCorrectorTests
{
    private static readonly SpellingCorrector Corrector = new(
    [
        "Has my last order shipped?", "Is my export ready?", "Show the dead letters", "Status of order", "card", "cart",
    ]);

    [Fact]
    public void Correct_FixesAMisspeltWordAgainstTheVocabulary()
    {
        Corrector.Correct("has my last order shiped").Should().Be("has my last order shipped");
    }

    [Fact]
    public void Correct_FixesASwappedPairOfLetters()
    {
        Corrector.Correct("is my exprot ready").Should().Be("is my export ready");
    }

    [Fact]
    public void Correct_ReturnsNull_WhenEveryWordIsKnown()
    {
        Corrector.Correct("Is my export ready?").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesShortWordsAlone()
    {
        Corrector.Correct("teh export").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesAnIdAlone()
    {
        Corrector.Correct("status of order 3f2a1b9c").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesQuotedTextAlone()
    {
        Corrector.Correct("status of \"shiped\"").Should().BeNull();
    }

    [Fact]
    public void Correct_RefusesATie()
    {
        // "carx" is one edit from both "card" and "cart": no guess.
        Corrector.Correct("carx").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesAWordWithNoCloseMatch()
    {
        Corrector.Correct("write a poem about rainfall").Should().BeNull();
    }

    [Theory]
    [InlineData("order", "order", 0)]
    [InlineData("shiped", "shipped", 1)]
    [InlineData("odrer", "order", 1)]
    [InlineData("abcdef", "uvwxyz", 3)]
    public void EditDistance_CountsInsertionsDeletionsSubstitutionsAndSwaps(string a, string b, int expected)
    {
        SpellingCorrector.EditDistance(a, b, limit: 2).Should().Be(Math.Min(expected, 3));
    }
}
