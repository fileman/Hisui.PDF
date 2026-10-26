using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System.Text;
using System.Text.RegularExpressions;
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
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
    }
    public Task<byte[]> AddTextWatermarkAsync(byte[] pdf, string text, WatermarkOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        options ??= new WatermarkOptions();

        return Task.Run(() => ModifyAllPages(pdf, (_, page) =>
        {
            RemoveWatermark(page);
            var before = page.Contents.Elements.Count;
            using (var gfx = PageGeometry.OpenGraphics(page))
            {
                var color = ColorWithAlpha(options.ColorHex, options.Opacity);
                var font = new XFont("Arial", options.FontSizePoints);
                var brush = new XSolidBrush(color);
                var (vw, vh) = PageGeometry.VisualSize(page);
                var center = new XPoint(vw / 2, vh / 2);

                gfx.Save();
                gfx.RotateAtTransform(options.RotationDegrees, center);
                gfx.DrawString(text, font, brush, center, XStringFormats.Center);
                gfx.Restore();
            }
            // The stream just appended is the watermark: tag it with its settings so it can be edited or removed later.
            if (page.Contents.Elements.Count > before && Resolve(page.Contents.Elements[^1]) is PdfDictionary added)
                added.Elements[WatermarkKey] = Describe(text, options);
        }), ct);
    }

    public Task<WatermarkInfo?> ReadWatermarkAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            foreach (var page in doc.Pages)
                foreach (var item in page.Contents.Elements)
                    if (Resolve(item) is PdfDictionary content && content.Elements.GetDictionary(WatermarkKey) is { } d)
                        return new WatermarkInfo(d.Elements.GetString("/Text"), new WatermarkOptions
                        {
                            Opacity = d.Elements.GetReal("/Opacity"),
                            FontSizePoints = d.Elements.GetReal("/Size"),
                            RotationDegrees = d.Elements.GetReal("/Rotation"),
                            ColorHex = d.Elements.GetString("/Color"),
                        });
            return (WatermarkInfo?)null;
        }, ct);
    }

    public Task<byte[]> RemoveWatermarkAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() => ModifyAllPages(pdf, (_, page) => RemoveWatermark(page)), ct);
    }

    // Second-class keys on the content streams this service appends, holding what they were drawn with so the
    // watermark and each free text can be read back, edited or removed later.
    private const string WatermarkKey = "/HisuiWatermark";
    private const string FreeTextKey = "/HisuiFreeText";

    private static PdfDictionary Describe(string text, WatermarkOptions options)
    {
        var d = new PdfDictionary();
        d.Elements["/Text"] = new PdfString(text, PdfStringEncoding.Unicode);
        d.Elements["/Opacity"] = new PdfReal(options.Opacity);
        d.Elements["/Size"] = new PdfReal(options.FontSizePoints);
        d.Elements["/Rotation"] = new PdfReal(options.RotationDegrees);
        d.Elements["/Color"] = new PdfString(options.ColorHex);
        return d;
    }

    private static void RemoveWatermark(PdfPage page) => RemoveTagged(page, WatermarkKey, null);

    /// <summary>
    /// Drops the content stream(s) of a page tagged with <paramref name="key"/> (and, when given, that <paramref name="id"/>).
    /// PDFsharp isolates each later addition by putting "q" in front of the first stream and "Q" after the last one, so
    /// such a stream may carry those operators: they are kept (as the stream's only content) to leave the graphics-state
    /// nesting balanced.
    /// </summary>
    private static void RemoveTagged(PdfPage page, string key, string? id)
    {
        var items = page.Contents.Elements;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (Resolve(items[i]) is not PdfDictionary content || content.Elements.GetDictionary(key) is not { } tag) continue;
            if (id is not null && tag.Elements.GetString("/Id") != id) continue;
            var balance = GraphicsStateBalance(content.Stream.UnfilteredValue);
            if (balance == 0)
            {
                items.RemoveAt(i);
                continue;
            }
            content.Elements.Remove(key);
            content.Elements.Remove("/Filter");
            content.Elements.Remove("/DecodeParms");
            content.Stream.Value = Encoding.ASCII.GetBytes(string.Join(' ', Enumerable.Repeat(balance > 0 ? "q" : "Q", Math.Abs(balance))));
        }
    }

    /// <summary>Number of "q" minus number of "Q" operators, string operands left out.</summary>
    private static int GraphicsStateBalance(byte[] content)
    {
        var text = Regex.Replace(Encoding.Latin1.GetString(content), @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>", " ");
        return Regex.Count(text, @"(?<!\S)q(?!\S)") - Regex.Count(text, @"(?<!\S)Q(?!\S)");
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : item;

    public Task<byte[]> AddHighlightAsync(byte[] pdf, int pageIndex, PdfRect rect, string colorHex = "#FFFF00", double opacity = 0.4, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = PageGeometry.OpenGraphics(page);
            var brush = new XSolidBrush(ColorWithAlpha(colorHex, opacity));
            gfx.DrawRectangle(brush, ToXRect(rect, page));
        }), ct);
    }

    public Task<byte[]> AddRectangleAsync(byte[] pdf, int pageIndex, PdfRect rect, string strokeHex = "#FF0000", string? fillHex = null, double lineWidthPoints = 2.0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = PageGeometry.OpenGraphics(page);
            var pen = new XPen(ParseColor(strokeHex), lineWidthPoints);
            var xRect = ToXRect(rect, page);

            if (fillHex is not null)
                gfx.DrawRectangle(pen, new XSolidBrush(ParseColor(fillHex)), xRect);
            else
                gfx.DrawRectangle(pen, xRect);
        }), ct);
    }

    public Task<byte[]> AddFreeTextAsync(byte[] pdf, int pageIndex, PdfRect rect, string text, string colorHex = "#000000", double fontSizePoints = 12,
        double charPitchPoints = 0, bool background = true, string? id = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            var before = page.Contents.Elements.Count;
            DrawFreeText(page, rect, text, colorHex, fontSizePoints, charPitchPoints, background);
            // Tag the stream just appended with what it was drawn with, so the text can be edited or moved later.
            if (page.Contents.Elements.Count > before && Resolve(page.Contents.Elements[^1]) is PdfDictionary added)
            {
                var d = new PdfDictionary();
                d.Elements["/Id"] = new PdfString(string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id);
                d.Elements["/Text"] = new PdfString(text, PdfStringEncoding.Unicode);
                d.Elements["/L"] = new PdfReal(rect.Left);
                d.Elements["/T"] = new PdfReal(rect.Top);
                d.Elements["/R"] = new PdfReal(rect.Right);
                d.Elements["/B"] = new PdfReal(rect.Bottom);
                d.Elements["/Size"] = new PdfReal(fontSizePoints);
                d.Elements["/Color"] = new PdfString(colorHex);
                d.Elements["/Pitch"] = new PdfReal(charPitchPoints);
                d.Elements["/Background"] = new PdfBoolean(background);
                added.Elements[FreeTextKey] = d;
            }
        }), ct);
    }

    public Task<IReadOnlyList<FreeTextInfo>> ReadFreeTextsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<FreeTextInfo>>(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if ((uint)pageIndex >= (uint)doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page {pageIndex} does not exist (page count = {doc.PageCount}).");
            var result = new List<FreeTextInfo>();
            foreach (var item in doc.Pages[pageIndex].Contents.Elements)
            {
                if (Resolve(item) is not PdfDictionary content || content.Elements.GetDictionary(FreeTextKey) is not { } d) continue;
                var e = d.Elements;
                result.Add(new FreeTextInfo(e.GetString("/Id"),
                    new PdfRect(e.GetReal("/L"), e.GetReal("/T"), e.GetReal("/R"), e.GetReal("/B")),
                    e.GetString("/Text"), e.GetReal("/Size"), e.GetString("/Color"), e.GetReal("/Pitch"), e.GetBoolean("/Background")));
            }
            return result;
        }, ct);
    }

    public Task<byte[]> RemoveFreeTextAsync(byte[] pdf, int pageIndex, string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) => RemoveTagged(page, FreeTextKey, id)), ct);
    }

    private static void DrawFreeText(PdfPage page, PdfRect rect, string text, string colorHex, double fontSizePoints, double charPitchPoints, bool background)
    {
        {
            using var gfx = PageGeometry.OpenGraphics(page);
            var xRect = ToXRect(rect, page);
            var font = new XFont("Arial", fontSizePoints);
            var brush = new XSolidBrush(ParseColor(colorHex));

            // Light background box so text is readable over page content (off when filling a printed form).
            if (background)
                gfx.DrawRectangle(
                    new XPen(XColors.DarkGray, 0.5),
                    new XSolidBrush(XColor.FromArgb(220, 255, 255, 240)),
                    xRect);

            // Clamp: a tiny box (or a near-zero drag) would make the padded inner rect negative,
            // which XRect rejects with WidthAndHeightCannotBeNegative.
            var inner = new XRect(xRect.Left + 3, xRect.Top + 3, Math.Max(0, xRect.Width - 6), Math.Max(0, xRect.Height - 6));
            var lineHeight = font.GetHeight();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var li = 0; li < lines.Length; li++)
            {
                var y = inner.Top + li * lineHeight;
                if (charPitchPoints <= 0)
                {
                    gfx.DrawString(lines[li], font, brush, new XPoint(inner.Left, y), XStringFormats.TopLeft);
                    continue;
                }
                // Boxed form: one cell per character, each glyph centred in its cell.
                var x = inner.Left;
                foreach (var rune in lines[li].EnumerateRunes())
                {
                    var glyph = rune.ToString();
                    var w = gfx.MeasureString(glyph, font).Width;
                    gfx.DrawString(glyph, font, brush, new XPoint(x + (charPitchPoints - w) / 2, y), XStringFormats.TopLeft);
                    x += charPitchPoints;
                }
            }
        }
    }

    public Task<byte[]> AddStickyNoteAsync(byte[] pdf, int pageIndex, double normalizedX, double normalizedY, string noteText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() => ModifyPage(pdf, pageIndex, (_, page) =>
        {
            using var gfx = PageGeometry.OpenGraphics(page);

            var (vw, vh) = PageGeometry.VisualSize(page);
            var noteW = vw * 0.18;
            var noteH = vh * 0.10;
            var noteX = normalizedX * vw - noteW / 2;
            var noteY = normalizedY * vh - noteH / 2;
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
            using var gfx = PageGeometry.OpenGraphics(page);
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
        doc.SaveStamped(ms);
        return ms.ToArray();
    }

    private static XRect ToXRect(PdfRect rect, PdfPage page)
    {
        var (w, h) = PageGeometry.VisualSize(page);
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
