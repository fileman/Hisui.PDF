using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Orchestrates OCR for scanned PDFs. Pages are detected as "scanned" (a raster image with no real
/// text layer), rasterized via <see cref="IPdfRenderer"/>, recognized through <see cref="IOcrEngine"/>,
/// and — for the searchable-PDF conversion — overlaid with an <em>invisible</em> text layer so the
/// scan looks identical but its text can be selected, searched and copied.
/// </summary>
internal sealed class PdfOcrService : IPdfOcrService
{
    // A page with fewer than this many non-whitespace characters of embedded text is treated as
    // having no real text layer (tolerates a stray header/footer artifact on an otherwise scanned page).
    private const int ScannedTextThreshold = 8;

    // PDFsharp 6.x needs an explicit font resolver before any text is drawn; the invisible OCR layer
    // is real text, so register the same shared resolver the annotation service uses.
    static PdfOcrService()
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
    }

    private readonly IPdfRenderer _renderer;
    private readonly IOcrEngine _engine;

    public PdfOcrService(IPdfRenderer renderer, IOcrEngine engine)
    {
        _renderer = renderer;
        _engine = engine;
    }

    public Task<IReadOnlyList<int>> DetectScannedPagesAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run<IReadOnlyList<int>>(() =>
        {
            using var doc = PigDocument.Open(pdf);
            var scanned = new List<int>();

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var textLength = page.Text?.Count(c => !char.IsWhiteSpace(c)) ?? 0;
                if (textLength >= ScannedTextThreshold) continue;

                if (HasRasterImage(page))
                    scanned.Add(page.Number - 1); // PdfPig is 1-based
            }

            return scanned;
        }, ct);
    }

    public async Task<OcrDocumentResult> RecognizeAsync(byte[] pdf, OcrOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new OcrOptions();

        var targets = await ResolveTargetPagesAsync(pdf, options, ct).ConfigureAwait(false);
        var pages = new List<OcrPageResult>(targets.Count);

        foreach (var pageIndex in targets)
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(await RecognizePageAsync(pdf, pageIndex, options, ct).ConfigureAwait(false));
        }

        return new OcrDocumentResult { Pages = pages };
    }

    public async Task<byte[]> MakeSearchableAsync(byte[] pdf, OcrOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new OcrOptions();

        var targets = await ResolveTargetPagesAsync(pdf, options, ct).ConfigureAwait(false);
        if (targets.Count == 0) return pdf; // nothing scanned — return the original bytes unchanged

        // Recognize every target page first (render + OCR), then write the text layer in one pass.
        var recognized = new Dictionary<int, OcrPageResult>(targets.Count);
        foreach (var pageIndex in targets)
        {
            ct.ThrowIfCancellationRequested();
            recognized[pageIndex] = await RecognizePageAsync(pdf, pageIndex, options, ct).ConfigureAwait(false);
        }

        return await Task.Run(() =>
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

            foreach (var (pageIndex, result) in recognized)
            {
                ct.ThrowIfCancellationRequested();
                if ((uint)pageIndex >= (uint)doc.PageCount) continue;
                DrawInvisibleTextLayer(doc.Pages[pageIndex], result.Words);
            }

            using var output = new MemoryStream();
            doc.Save(output);
            return output.ToArray();
        }, ct).ConfigureAwait(false);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<int>> ResolveTargetPagesAsync(byte[] pdf, OcrOptions options, CancellationToken ct)
    {
        if (options.OnlyScannedPages)
            return await DetectScannedPagesAsync(pdf, ct).ConfigureAwait(false);

        var count = await _renderer.GetPageCountAsync(pdf, ct).ConfigureAwait(false);
        return [.. Enumerable.Range(0, count)];
    }

    private async Task<OcrPageResult> RecognizePageAsync(byte[] pdf, int pageIndex, OcrOptions options, CancellationToken ct)
    {
        var png = await _renderer.RenderPageToPngAsync(pdf, pageIndex, options.Dpi, ct).ConfigureAwait(false);
        return await _engine.RecognizeAsync(png, pageIndex, options, ct).ConfigureAwait(false);
    }

    private static bool HasRasterImage(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return page.GetImages().Any();
        }
        catch
        {
            // A malformed image XObject must not abort detection of the rest of the document.
            return false;
        }
    }

    /// <summary>
    /// Draws each OCR word as fully transparent text positioned over the scanned image. The glyphs
    /// are emitted into the content stream (so viewers and extractors can select/search them) but are
    /// invisible, leaving the page visually unchanged.
    /// </summary>
    private static void DrawInvisibleTextLayer(PdfPage page, IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0) return;

        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        double pageW = page.Width.Point, pageH = page.Height.Point;

        foreach (var word in words)
        {
            var box = word.BoundingBox;
            double boxW = box.Width * pageW, boxH = box.Height * pageH;
            if (boxW <= 0 || boxH <= 0 || string.IsNullOrWhiteSpace(word.Text)) continue;

            // Size the font to roughly fill the recognized box; clamp away from degenerate sizes.
            var fontSize = Math.Clamp(boxH * 0.8, 1.0, 1000.0);
            var font = new XFont("Arial", fontSize);
            var rect = new XRect(box.Left * pageW, box.Top * pageH, boxW, boxH);
            gfx.DrawString(word.Text, font, invisible, rect, XStringFormats.TopLeft);
        }
    }
}
