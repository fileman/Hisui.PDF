using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Annotation/markup operations that burn content directly into the page content stream via
/// PDFsharp XGraphics. This avoids the complexity of the /Annots dictionary and ensures
/// compatibility with any PDF viewer and any downstream merge/split operation.
/// </summary>
internal sealed class PdfAnnotationService : IPdfAnnotationService
{
    // PDFsharp 6.x requires an explicit font resolver before any text rendering.
    // The static constructor runs once per AppDomain, including in test hosts.
    static PdfAnnotationService()
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }
    public Task<byte[]> AddTextWatermarkAsync(byte[] pdf, string text, WatermarkOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        options ??= new WatermarkOptions();

        return Task.Run(() => ModifyAllPages(pdf, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var color = ColorWithAlpha(options.ColorHex, options.Opacity);
            var font = new XFont("Arial", options.FontSizePoints);
            var brush = new XSolidBrush(color);
            var center = new XPoint(page.Width.Point / 2, page.Height.Point / 2);

            gfx.Save();
            gfx.RotateAtTransform(options.RotationDegrees, center);
            gfx.DrawString(text, font, brush, center, XStringFormats.Center);
            gfx.Restore();
        }), ct);
    }

    public Task<byte[]> AddHighlightAsync(byte[] pdf, int pageIndex, PdfRect rect, string colorHex = "#FFFF00", double opacity = 0.4, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var brush = new XSolidBrush(ColorWithAlpha(colorHex, opacity));
            gfx.DrawRectangle(brush, ToXRect(rect, page));
        }), ct);
    }

    public Task<byte[]> AddRectangleAsync(byte[] pdf, int pageIndex, PdfRect rect, string strokeHex = "#FF0000", string? fillHex = null, double lineWidthPoints = 2.0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var pen = new XPen(ParseColor(strokeHex), lineWidthPoints);
            var xRect = ToXRect(rect, page);

            if (fillHex is not null)
                gfx.DrawRectangle(pen, new XSolidBrush(ParseColor(fillHex)), xRect);
            else
                gfx.DrawRectangle(pen, xRect);
        }), ct);
    }

    public Task<byte[]> AddFreeTextAsync(byte[] pdf, int pageIndex, PdfRect rect, string text, string colorHex = "#000000", double fontSizePoints = 12, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var xRect = ToXRect(rect, page);
            var font = new XFont("Arial", fontSizePoints);
            var brush = new XSolidBrush(ParseColor(colorHex));

            // Light background box so text is readable over page content.
            gfx.DrawRectangle(
                new XPen(XColors.DarkGray, 0.5),
                new XSolidBrush(XColor.FromArgb(220, 255, 255, 240)),
                xRect);
            gfx.DrawString(
                text, font, brush,
                // Clamp: a tiny box (or a near-zero drag) would make the padded inner rect negative,
                // which XRect rejects with WidthAndHeightCannotBeNegative.
                new XRect(xRect.Left + 3, xRect.Top + 3,
                          Math.Max(0, xRect.Width - 6), Math.Max(0, xRect.Height - 6)),
                XStringFormats.TopLeft);
        }), ct);
    }

    public Task<byte[]> AddStickyNoteAsync(byte[] pdf, int pageIndex, double normalizedX, double normalizedY, string noteText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var noteW = page.Width.Point * 0.18;
            var noteH = page.Height.Point * 0.10;
            var noteX = normalizedX * page.Width.Point - noteW / 2;
            var noteY = normalizedY * page.Height.Point - noteH / 2;
            var noteRect = new XRect(noteX, noteY, noteW, noteH);

            gfx.DrawRectangle(
                new XPen(XColor.FromArgb(180, 160, 0), 1),
                new XSolidBrush(XColor.FromArgb(210, 255, 255, 130)),
                noteRect);

            var font = new XFont("Arial", 8);
            var textRect = new XRect(noteX + 3, noteY + 3,
                Math.Max(0, noteW - 6), Math.Max(0, noteH - 6));
            gfx.DrawString(noteText, font, XBrushes.Black, textRect, XStringFormats.TopLeft);
        }), ct);
    }

    public Task<byte[]> AddImageOverlayAsync(byte[] pdf, int pageIndex, byte[] imageBytes, PdfRect rect, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(imageBytes);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            using var imgStream = new MemoryStream(imageBytes);
            var xImg = XImage.FromStream(imgStream);
            try
            {
                gfx.DrawImage(xImg, ToXRect(rect, page));
            }
            finally
            {
                xImg.Dispose();
            }
        }), ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static byte[] ModifyAllPages(byte[] pdf, Action<PdfDocument, PdfPage> perPage)
    {
        using var inputStream = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);
        for (var i = 0; i < doc.PageCount; i++)
            perPage(doc, doc.Pages[i]);
        return Save(doc);
    }

    private static byte[] ModifyPage(byte[] pdf, int pageIndex, Action<PdfDocument, PdfPage> action)
    {
        using var inputStream = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);
        if ((uint)pageIndex >= (uint)doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex),
                $"Page {pageIndex} does not exist (page count = {doc.PageCount}).");
        action(doc, doc.Pages[pageIndex]);
        return Save(doc);
    }

    private static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static XRect ToXRect(PdfRect rect, PdfPage page)
    {
        var w = page.Width.Point;
        var h = page.Height.Point;
        return new XRect(rect.Left * w, rect.Top * h, rect.Width * w, rect.Height * h);
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        return (Convert.ToInt32(s[..2], 16), Convert.ToInt32(s[2..4], 16), Convert.ToInt32(s[4..6], 16));
    }

    private static XColor ParseColor(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return XColor.FromArgb(r, g, b);
    }

    private static XColor ColorWithAlpha(string hex, double opacity)
    {
        var (r, g, b) = ParseHex(hex);
        return XColor.FromArgb((int)(opacity * 255), r, g, b);
    }
}
