using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Implements "redact + retype" text replacement: covers the original word with a white rectangle,
/// then draws new text at the same position. Font appearance may differ slightly when the PDF embeds a
/// subset font. Find-and-replace (<see cref="ApplyReplacementsAsync"/>) additionally removes the old
/// glyphs from the content stream (<see cref="ContentTextRemover"/>), covering only what it cannot remove.
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
            using (var gfx = PageGeometry.OpenGraphics(page))
                CoverAndRetype(gfx, page, [wordBounds], wordBounds, newText, fontSizePoints, RetypeStyle.Plain);

            using var outMs = new MemoryStream();
            doc.SaveStamped(outMs);
            return outMs.ToArray();
        }, ct);
    }

    public Task<byte[]> ApplyReplacementsAsync(
        byte[] pdf,
        IReadOnlyList<TextReplacement> replacements,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return Task.FromResult(pdf);

        return Task.Run(() =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);

            foreach (var group in replacements.GroupBy(r => r.PageIndex))
            {
                ct.ThrowIfCancellationRequested();
                if ((uint)group.Key >= (uint)doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(replacements),
                        $"Page {group.Key} does not exist (count = {doc.PageCount}).");

                var page = doc.Pages[group.Key];
                var edits = group.Where(r => r.Covers.Count > 0).ToList();

                // Take the old glyphs out of the content stream first, so the replaced text no longer
                // extracts or searches; boxes are padded like the white cover so whole glyphs qualify.
                var media = page.MediaBox;
                var regions = edits.SelectMany((r, i) => r.Covers.Select(box =>
                {
                    var raw = PageGeometry.ToRaw(page, box);
                    return new ContentTextRemover.Region(
                        raw.X1 + media.X1, raw.Y1 + media.Y1, raw.X2 + media.X1, raw.Y2 + media.Y1, i);
                })).ToList();
                var removed = ContentTextRemover.Remove(page, regions, edits.Count);

                // One graphics context per page: each XGraphics appends its own content stream.
                using var gfx = PageGeometry.OpenGraphics(page);
                for (var i = 0; i < edits.Count; i++)
                {
                    var r = edits[i];
                    // Paint over whatever could not be removed (unsupported font, text in a form XObject…).
                    var cover = removed[i] < r.OriginalText.Count(c => !char.IsWhiteSpace(c));
                    CoverAndRetype(gfx, page, cover ? r.Covers : [], r.Covers[0], r.NewText, r.FontSizePoints,
                        RetypeStyle.For(r, page));
                }
            }

            using var outMs = new MemoryStream();
            doc.SaveStamped(outMs);
            return outMs.ToArray();
        }, ct);
    }

    /// <summary>Paints every box white, then draws <paramref name="text"/> on the <paramref name="anchor"/>'s baseline.</summary>
    private static void CoverAndRetype(XGraphics gfx, PdfPage page, IReadOnlyList<PdfRect> boxes, PdfRect anchor, string text, double fontSizePoints, RetypeStyle style)
    {
        // The glyph-tight boxes leave antialiased edges, side bearings and descenders (g, p, y) just
        // outside. Inflate each cover rectangle — with extra room at the bottom for descenders — so
        // the original text is fully painted over.
        var pad = Math.Max(fontSizePoints * 0.15, 1.0);
        foreach (var box in boxes)
        {
            var r = ToXRect(box, page);
            gfx.DrawRectangle(XBrushes.White, new XRect(r.X - pad, r.Y - pad * 0.5, r.Width + pad * 2, r.Height + pad * 1.5));
        }
        if (text.Length == 0) return;

        // Redraw on the original baseline (≈ bottom of the glyph-tight box) so the replacement
        // sits on the same line as the surrounding text instead of shifting upward.
        var origin = ToXRect(anchor, page);
        var font = new XFont("Arial", Math.Max(fontSizePoints, 4), style.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        var format = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.BaseLine,
        };
        var c = style.ColorArgb;
        var brush = new XSolidBrush(XColor.FromArgb((int)(c >> 24), (int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF)));
        var at = new XPoint(origin.X, origin.Bottom);

        // Arial is wider than many document fonts (condensed headings especially): squeeze it horizontally
        // so the new text takes about the room the original letters had, never stretching it wider.
        var natural = gfx.MeasureString(text, font).Width;
        var squeeze = style.TargetWidth is double target && natural > target * 1.05
            ? Math.Max(0.55, target / natural)
            : 1.0;
        if (squeeze < 1.0)
        {
            var state = gfx.Save();
            gfx.ScaleAtTransform(squeeze, 1, at);
            gfx.DrawString(text, font, brush, at, format);
            gfx.Restore(state);
        }
        else gfx.DrawString(text, font, brush, at, format);
    }

    /// <summary>How retyped text should look: the original word's colour and weight, and the width to aim for.</summary>
    private readonly record struct RetypeStyle(uint ColorArgb, bool Bold, double? TargetWidth)
    {
        public static readonly RetypeStyle Plain = new(0xFF000000, false, null);

        /// <summary>
        /// The original words' width scaled by how many letters the new text has compared with the old one,
        /// i.e. the room the new text would take in the original font.
        /// </summary>
        public static RetypeStyle For(TextReplacement r, PdfPage page)
        {
            var oldLetters = r.OriginalText.Count(ch => !char.IsWhiteSpace(ch));
            var newLetters = r.NewText.Count(ch => !char.IsWhiteSpace(ch)) + r.NewText.Count(char.IsWhiteSpace) * 0.5;
            var width = r.Covers.Sum(b => ToXRect(b, page).Width);
            double? target = oldLetters > 0 && width > 0 ? width * newLetters / oldLetters : null;
            return new RetypeStyle(r.ColorArgb, r.IsBold, target);
        }
    }

    private static XRect ToXRect(PdfRect rect, PdfPage page)
    {
        var (w, h) = PageGeometry.VisualSize(page);
        return new XRect(rect.Left * w, rect.Top * h, rect.Width * w, rect.Height * h);
    }
}
