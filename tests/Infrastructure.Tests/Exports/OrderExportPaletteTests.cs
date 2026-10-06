using AiFramework.Infrastructure.Exports;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Exports;

/// <summary>The spec's promise that every text/background pair in the PDF meets WCAG AA (4.5:1).</summary>
public sealed class OrderExportPaletteTests
{
    public static TheoryData<string> Pairs() => [.. OrderExportPalette.TextPairs.Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TextPair_MeetsWcagAa(string name)
    {
        var pair = OrderExportPalette.TextPairs.Single(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        Contrast(pair.Text, pair.Background).Should().BeGreaterThanOrEqualTo(4.5, name);
    }

    private static double Contrast(PaletteColor a, PaletteColor b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(PaletteColor c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var v = value / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
