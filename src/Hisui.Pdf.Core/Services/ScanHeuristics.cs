using UglyToad.PdfPig.Content;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Decides whether a page "looks scanned": a raster image with no real text layer. Shared by OCR (which
/// pages to recognize) and the Word exporter (which pages to rebuild from OCR instead of the text layer).
/// </summary>
internal static class ScanHeuristics
{
    // Fewer than this many horizontal non-whitespace characters means no real text layer at all.
    private const int NoTextThreshold = 8;

    // A page mostly covered by images still counts as scanned when its horizontal text stays below this:
    // a protocol stamp, a page number or a filename footer, not a body of text.
    private const int SparseTextThreshold = 40;
    private const double CoveredFraction = 0.6;

    public static bool IsScannedPage(Page page)
    {
        var images = Images(page);
        if (images.Count == 0) return false;

        var horizontal = HorizontalCharCount(page);
        if (horizontal < NoTextThreshold) return true;
        return horizontal < SparseTextThreshold && ImageCoverage(images, page.Width, page.Height) >= CoveredFraction;
    }

    /// <summary>
    /// Non-whitespace letters that run left to right (or upside down). Rotated text such as a vertical
    /// stamp in the margin is not body text and must not hide a scan from OCR.
    /// </summary>
    private static int HorizontalCharCount(Page page)
    {
        try
        {
            return page.Letters.Count(l =>
                (l.TextOrientation is TextOrientation.Horizontal or TextOrientation.Rotate180)
                && !string.IsNullOrWhiteSpace(l.Value));
        }
        catch
        {
            return (page.Text ?? string.Empty).Count(c => !char.IsWhiteSpace(c));
        }
    }

    private static List<UglyToad.PdfPig.Core.PdfRectangle> Images(Page page)
    {
        try
        {
            return [.. page.GetImages().Select(i => i.BoundingBox)];
        }
        catch
        {
            // A malformed image XObject must not abort detection of the rest of the document.
            return [];
        }
    }

    /// <summary>Fraction of the page covered by the union of the image boxes (sampled on a coarse grid).</summary>
    private static double ImageCoverage(List<UglyToad.PdfPig.Core.PdfRectangle> boxes, double width, double height)
    {
        if (width <= 0 || height <= 0) return 0;
        const int n = 50;
        var covered = 0;
        for (var gy = 0; gy < n; gy++)
        for (var gx = 0; gx < n; gx++)
        {
            double x = (gx + 0.5) * width / n, y = (gy + 0.5) * height / n;
            if (boxes.Any(b => x >= Math.Min(b.Left, b.Right) && x <= Math.Max(b.Left, b.Right)
                            && y >= Math.Min(b.Bottom, b.Top) && y <= Math.Max(b.Bottom, b.Top))) covered++;
        }
        return covered / (double)(n * n);
    }
}
