using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class FuzzyTests
{
    [Fact]
    public void WordSimilarity_IsOne_ForANameThatAppearsExactly()
    {
        Fuzzy.WordSimilarity("Copenhagen", "What is the population of Copenhagen?").Should().Be(1);
    }

    [Fact]
    public void WordSimilarity_StaysHigh_ForATypo()
    {
        Fuzzy.WordSimilarity("Copenhagen", "population of copenhagn").Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void WordSimilarity_FindsAPartOfALongerName()
    {
        Fuzzy.WordSimilarity("All Denmark", "What is the population of Denmark?").Should().BeGreaterThan(0.6);
    }

    [Fact]
    public void WordSimilarity_IsLow_ForAnUnrelatedName()
    {
        Fuzzy.WordSimilarity("Region Hovedstaden", "What is the population of Denmark?").Should().BeLessThan(0.3);
    }

    [Fact]
    public void Fold_SpellsOutDanishLetters()
    {
        Fuzzy.Fold("København Ærø Ålborg").Should().Be("kobenhavn aero aalborg");
    }
}
