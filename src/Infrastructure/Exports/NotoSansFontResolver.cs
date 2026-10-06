using System.Reflection;
using PdfSharp.Fonts;

namespace AiFramework.Infrastructure.Exports;

/// <summary>
/// Serves the embedded Noto Sans for <b>every</b> family name. The worker image has no system
/// fonts, and MigraDoc asks for "Courier New" as its own error font before it draws anything, so a
/// resolver that answered only "Noto Sans" throws on the first render (found by a spike, 2026-10-06).
/// Italic is simulated: no italic face is shipped.
/// </summary>
internal sealed class NotoSansFontResolver : IFontResolver
{
    public const string FamilyName = "Noto Sans";

    private static readonly Lock InstallLock = new();

    private NotoSansFontResolver()
    {
    }

    /// <summary>
    /// PDFsharp's resolver is process-wide, and the Api, the worker and test hosts can share one
    /// process, so it is set once. A resolver someone else installed is never replaced: PDFsharp can
    /// throw on a change after fonts were used, and silently taking it over would break that caller.
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            switch (GlobalFontSettings.FontResolver)
            {
                case null:
                    GlobalFontSettings.FontResolver = new NotoSansFontResolver();
                    break;
                case NotoSansFontResolver:
                    break;
                case var other:
                    throw new InvalidOperationException(
                        $"PDFsharp's font resolver is already {other.GetType().FullName}; the order export needs " +
                        $"{nameof(NotoSansFontResolver)}, and replacing a resolver in use is not safe.");
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "NotoSans-Bold" : "NotoSans-Regular", mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ExportFonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"The embedded font '{faceName}' is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
