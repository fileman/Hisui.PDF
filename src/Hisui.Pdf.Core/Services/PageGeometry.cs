using PdfSharp.Drawing;
using Hisui.Pdf.Core.Model;
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

    /// <summary>Visual (rendered, normalised) rect → raw PDF user space (origin bottom-left), honouring /Rotate.</summary>
    public static PdfRectangle ToRaw(PdfPage page, PdfRect rect)
    {
        var (vw, vh) = VisualSize(page);
        var corner1 = new XPoint(rect.Left * vw, rect.Top * vh);
        var corner2 = new XPoint(rect.Right * vw, rect.Bottom * vh);
        if (VisualToRaw(page) is { } toRaw)
        {
            corner1 = toRaw.Transform(corner1);
            corner2 = toRaw.Transform(corner2);
        }
        var pageH = page.Height.Point;
        return new PdfRectangle(
            new XPoint(Math.Min(corner1.X, corner2.X), pageH - Math.Max(corner1.Y, corner2.Y)),
            new XPoint(Math.Max(corner1.X, corner2.X), pageH - Math.Min(corner1.Y, corner2.Y)));
    }

    /// <summary>Inverse of <see cref="ToRaw"/>.</summary>
    public static PdfRect ToVisual(PdfPage page, PdfRectangle raw)
    {
        var (vw, vh) = VisualSize(page);
        var pageH = page.Height.Point;
        var p1 = new XPoint(raw.X1, pageH - raw.Y1);
        var p2 = new XPoint(raw.X2, pageH - raw.Y2);
        if (VisualToRaw(page) is { } toRaw)
        {
            var toVisual = toRaw;
            toVisual.Invert();
            p1 = toVisual.Transform(p1);
            p2 = toVisual.Transform(p2);
        }
        return new PdfRect(Math.Min(p1.X, p2.X) / vw, Math.Min(p1.Y, p2.Y) / vh,
                           Math.Max(p1.X, p2.X) / vw, Math.Max(p1.Y, p2.Y) / vh);
    }

    /// <summary>Graphics (append mode) whose coordinates are the rendered orientation.</summary>
    public static XGraphics OpenGraphics(PdfPage page)
    {
        var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        if (VisualToRaw(page) is { } m) gfx.MultiplyTransform(m, XMatrixOrder.Prepend);
        return gfx;
    }

    /// <summary>
    /// PdfPig 0.1.x splits words on pages with /Rotate (letters come back in rotated coordinates but the
    /// word extractor assumes horizontal text). So words are read from a copy with the rotation removed,
    /// and boxes are rotated back to the rendered orientation by the caller. Returns the original bytes
    /// and no rotations when nothing is rotated or the document can't be rewritten.
    /// </summary>
    public static (byte[] Pdf, int[] Rotations) Unrotate(byte[] pdf)
    {
        try
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            var rotations = new int[doc.PageCount];
            var any = false;
            for (var i = 0; i < rotations.Length; i++)
            {
                rotations[i] = ((doc.Pages[i].Rotate % 360) + 360) % 360;
                if (rotations[i] == 0) continue;
                any = true;
                doc.Pages[i].Rotate = 0;
            }
            if (!any) return (pdf, rotations);

            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return (output.ToArray(), rotations);
        }
        catch (Exception)
        {
            return (pdf, []);
        }
    }
}
