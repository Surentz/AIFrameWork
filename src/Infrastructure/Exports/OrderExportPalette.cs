using System.Runtime.InteropServices;
using MigraDoc.DocumentObjectModel;

namespace AiFramework.Infrastructure.Exports;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct PaletteColor(byte R, byte G, byte B)
{
    public Color ToColor() => new(R, G, B);
}

/// <summary>
/// The export PDF's colours: the app's light theme, copied from frontend/src/styles/tokens.css
/// because the backend cannot read CSS. A palette change there does not reach the PDF by itself.
/// OrderExportPaletteTests holds every pair in <see cref="TextPairs"/> to WCAG AA.
/// </summary>
internal static class OrderExportPalette
{
    public static readonly PaletteColor Brand = new(0x3b, 0x2f, 0xa8);           // --color-brand-from
    public static readonly PaletteColor OnBrand = new(0xff, 0xff, 0xff);
    public static readonly PaletteColor OnBrandSoft = new(0xe0, 0xdd, 0xf7);     // solid stand-in for 72% white
    public static readonly PaletteColor Card = new(0xee, 0xf1, 0xff);            // --color-accent-soft
    public static readonly PaletteColor Text = new(0x12, 0x15, 0x1c);            // --color-text
    public static readonly PaletteColor Muted = new(0x59, 0x61, 0x6f);           // --color-text-muted
    public static readonly PaletteColor Sunken = new(0xf8, 0xf9, 0xfb);          // --color-surface-sunken
    public static readonly PaletteColor Surface = new(0xff, 0xff, 0xff);         // --color-surface
    public static readonly PaletteColor Hairline = new(0xe4, 0xe7, 0xec);        // --color-border
    public static readonly PaletteColor Rule = new(0xcf, 0xd4, 0xdd);            // --color-border-strong
    public static readonly PaletteColor ShippedText = new(0x0f, 0x6b, 0x3f);     // --color-success-text
    public static readonly PaletteColor ShippedBackground = new(0xf0, 0xfd, 0xf5);
    public static readonly PaletteColor CancelledText = new(0x91, 0x20, 0x18);   // --color-danger-text
    public static readonly PaletteColor CancelledBackground = new(0xfe, 0xf4, 0xf3);
    public static readonly PaletteColor PlacedText = new(0x4f, 0x46, 0xe5);      // --color-accent
    public static readonly PaletteColor PlacedBackground = new(0xee, 0xf1, 0xff);

    /// <summary>Every text colour the renderer draws, on every background it draws it on.</summary>
    public static readonly IReadOnlyList<(string Name, PaletteColor Text, PaletteColor Background)> TextPairs =
    [
        ("title on brand", OnBrand, Brand),
        ("subtitle on brand", OnBrandSoft, Brand),
        ("card label", Muted, Card),
        ("card value", Text, Card),
        ("table header", Muted, Sunken),
        ("row text", Text, Surface),
        ("zebra row text", Text, Sunken),
        ("secondary row text", Muted, Surface),
        ("secondary zebra row text", Muted, Sunken),
        ("footer", Muted, Surface),
        ("shipped badge", ShippedText, ShippedBackground),
        ("cancelled badge", CancelledText, CancelledBackground),
        ("placed badge", PlacedText, PlacedBackground),
    ];
}
