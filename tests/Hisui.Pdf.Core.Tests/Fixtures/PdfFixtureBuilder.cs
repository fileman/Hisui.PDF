using Hisui.Pdf.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Hisui.Pdf.Core.Tests.Fixtures;

/// <summary>
/// Builds small, deterministic PDFs for tests. Pages are drawn with vector shapes only (no text), so
/// the PDFsharp "Core" build needs no font resolver. A text-capable variant is added in the
/// extraction phase, where a bundled font resolver is wired up.
/// </summary>
public static class PdfFixtureBuilder
{
    /// <summary>Creates an A4 PDF with <paramref name="pageCount"/> pages, each visually distinct.</summary>
    public static byte[] Create(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        using var document = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;

            using var gfx = XGraphics.FromPdfPage(page);
            // A filled bar whose position shifts per page — enough to tell pages apart after round-trips.
            gfx.DrawRectangle(XBrushes.SteelBlue, 40, 40 + (i * 12 % 200), 160, 60);
            gfx.DrawRectangle(XPens.Black, 20, 20, page.Width.Point - 40, page.Height.Point - 40);
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Creates an A4 PDF with <paramref name="pageCount"/> pages, each containing the supplied text.
    /// Requires the Windows font resolver — configured automatically via <see cref="PdfAnnotationService"/>'s
    /// static constructor (which fires when the test assembly first references that type).
    /// </summary>
    public static byte[] CreateWithText(string text, int pageCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        // Ensure font resolver is active (harmless no-op if already set by PdfAnnotationService's static ctor).
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();

        using var document = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14);
            gfx.DrawString($"{text} (p{i + 1})", font, XBrushes.Black, new XPoint(50, 100 + i * 20));
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }
}
