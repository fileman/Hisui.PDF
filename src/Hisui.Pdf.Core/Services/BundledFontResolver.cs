using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Font resolver for PDFsharp 6.x. Every family Hisui PDF asks for ("Arial" throughout) resolves to the bundled
/// Liberation Sans (SIL OFL 1.1, metric-compatible with Arial), so the text written into documents looks the same on
/// every platform and never embeds a proprietary system font.
/// </summary>
internal sealed class BundledFontResolver : IFontResolver
{
    private const string Regular = "LiberationSans-Regular";

    // Face name → font file bytes, read once from the assembly resources (PDFsharp asks per document).
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    public string DefaultFaceName => Regular;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new((isBold, isItalic) switch
        {
            (true, true) => "LiberationSans-BoldItalic",
            (true, false) => "LiberationSans-Bold",
            (false, true) => "LiberationSans-Italic",
            _ => Regular,
        });

    public byte[]? GetFont(string faceName) => Cache.GetOrAdd(faceName, Load);

    private static byte[] Load(string faceName)
    {
        using var stream = typeof(BundledFontResolver).Assembly.GetManifestResourceStream($"Hisui.Pdf.Core.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"Embedded font '{faceName}' is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
