using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Maps between the page as the user sees it (rendered orientation, /Rotate applied, top-left origin)
/// and the page's raw, unrotated user space that PDFsharp draws in.
/// </summary>
internal static class PageGeometry
{
    public static int Rotation(PdfPage page) => ((page.Rotate % 360) + 360) % 360;

    /// <summary>Page size in points as rendered (width/height swapped for 90°/270°).</summary>
    public static (double W, double H) VisualSize(PdfPage page)
    {
        var w = page.Width.Point;
        var h = page.Height.Point;
        return Rotation(page) is 90 or 270 ? (h, w) : (w, h);
    }

    /// <summary>Matrix taking visual top-left coordinates to raw top-left coordinates; null at 0°.</summary>
    public static XMatrix? VisualToRaw(PdfPage page)
    {
        var w = page.Width.Point;
        var h = page.Height.Point;
        return Rotation(page) switch
        {
            90  => new XMatrix(0, -1, 1, 0, 0, h),
            180 => new XMatrix(-1, 0, 0, -1, w, h),
            270 => new XMatrix(0, 1, -1, 0, w, 0),
            _   => null,
        };
    }

    /// <summary>Graphics (append mode) whose coordinates are the rendered orientation.</summary>
    public static XGraphics OpenGraphics(PdfPage page)
    {
        var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        if (VisualToRaw(page) is { } m) gfx.MultiplyTransform(m, XMatrixOrder.Prepend);
        return gfx;
    }
}
