using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Implements "redact + retype" text replacement: covers the original word with a white rectangle,
/// then draws new text at the same position. Not a content-stream edit; font appearance may differ
/// slightly when the PDF embeds a subset font.
/// </summary>
internal sealed class PdfTextEditService : IPdfTextEditService
{
    public Task<byte[]> ReplaceWordAsync(
        byte[] pdf,
        int pageIndex,
        PdfRect wordBounds,
        string newText,
        double fontSizePoints,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(newText);

        return Task.Run(() =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);

            if ((uint)pageIndex >= (uint)doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"Page {pageIndex} does not exist (count = {doc.PageCount}).");

            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var xRect = ToXRect(wordBounds, page);

            // The PdfPig bounding box is glyph-tight, so antialiased edges, side bearings and
            // descenders (g, p, y) can fall just outside it. Inflate the cover rectangle — with
            // extra room at the bottom for descenders — so the original text is fully painted over.
            var pad = Math.Max(fontSizePoints * 0.15, 1.0);
            var cover = new XRect(
                xRect.X - pad,
                xRect.Y - pad * 0.5,
                xRect.Width + pad * 2,
                xRect.Height + pad * 1.5);
            gfx.DrawRectangle(XBrushes.White, cover);

            // Redraw on the original baseline (≈ bottom of the glyph-tight box) so the replacement
            // sits on the same line as the surrounding text instead of shifting upward.
            var font = new XFont("Arial", Math.Max(fontSizePoints, 4));
            var format = new XStringFormat
            {
                Alignment = XStringAlignment.Near,
                LineAlignment = XLineAlignment.BaseLine,
            };
            gfx.DrawString(newText, font, XBrushes.Black, new XPoint(xRect.X, xRect.Bottom), format);

            using var outMs = new MemoryStream();
            doc.Save(outMs);
            return outMs.ToArray();
        }, ct);
    }

    private static XRect ToXRect(PdfRect rect, PdfPage page)
    {
        var w = page.Width.Point;
        var h = page.Height.Point;
        return new XRect(rect.Left * w, rect.Top * h, rect.Width * w, rect.Height * h);
    }
}
