using System.Globalization;
using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
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
                DrawInvisibleTextLayer(doc, doc.Pages[pageIndex], result.Words);
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
    /// Writes each OCR word as an <em>invisible</em> text run (PDF text rendering mode 3) positioned over
    /// the scanned image. The glyphs are emitted into a fresh content stream so viewers and extractors can
    /// select / search / copy them, but mode 3 paints nothing, leaving the page visually unchanged.
    /// A transparent fill colour is deliberately NOT used: PDF renderers paint alpha-0 text as solid black.
    /// </summary>
    private static void DrawInvisibleTextLayer(PdfDocument doc, PdfPage page, IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0) return;

        const string fontName = "HisuiOcr";
        EnsureStandardFont(doc, page, fontName);

        // OCR boxes are normalized against the UPRIGHT rendered image. PDFium renders the page's effective
        // box (CropBox ∩ MediaBox) with /Rotate baked in, so map that box — at its content-space origin and
        // size — back into unrotated content space with a cm transform, for correct alignment at any rotation.
        var (boxX, boxY, boxW0, boxH0) = EffectiveRenderBox(page);
        var (displayW, displayH, cm) = RotationTransform(page.Rotate, boxX, boxY, boxW0, boxH0);

        var sb = new StringBuilder();
        sb.Append("q\n").Append(cm).Append(" cm\nBT\n3 Tr\n"); // 3 Tr = invisible (selectable, not painted)
        foreach (var word in words)
        {
            var box = word.BoundingBox;
            double boxW = (box.Right - box.Left) * displayW, boxH = (box.Bottom - box.Top) * displayH;
            if (boxW <= 0 || boxH <= 0 || string.IsNullOrWhiteSpace(word.Text)) continue;

            // Display space is bottom-left origin; the OCR box is top-left normalized. Size to the box
            // height and place the baseline a little above the box bottom to sit under the glyphs.
            double fontSize = Math.Clamp(boxH * 0.9, 1.0, 1000.0);
            double x = box.Left * displayW;
            double baseline = displayH - box.Bottom * displayH + boxH * 0.15;

            // Stretch the run horizontally so the selectable text spans the recognized word box
            // (Helvetica averages ~0.5 em per glyph). Keeps search-highlight rectangles aligned.
            double natural = 0.5 * fontSize * word.Text.Length;
            double tz = natural > 0 ? Math.Clamp(boxW / natural * 100.0, 10.0, 1000.0) : 100.0;

            sb.Append('/').Append(fontName).Append(' ').Append(F(fontSize)).Append(" Tf\n");
            sb.Append(F(tz)).Append(" Tz\n");
            sb.Append("1 0 0 1 ").Append(F(x)).Append(' ').Append(F(baseline)).Append(" Tm\n");
            sb.Append('(').Append(EscapePdfText(word.Text)).Append(") Tj\n");
        }
        sb.Append("ET\nQ\n");

        var content = page.Contents.AppendContent();
        content.CreateStream(EncodeWinAnsi(sb.ToString()));
    }

    /// <summary>
    /// The content-space box PDFium actually rasterizes: the CropBox clamped to the MediaBox, falling back to
    /// the MediaBox when no (or a degenerate) CropBox is present. Returns its lower-left origin and size; OCR
    /// boxes are normalized against an image of exactly this region, so the text layer must use it (not the
    /// MediaBox) to stay aligned when a page is cropped or its MediaBox origin is not at (0,0).
    /// </summary>
    private static (double X, double Y, double W, double H) EffectiveRenderBox(PdfPage page)
    {
        // Read the boxes from the raw dictionary (walking /Parent for inheritance) rather than the
        // PdfPage.MediaBox/CropBox getters: the CropBox getter MUTATES the page, writing a degenerate
        // [0 0 0 0] CropBox onto pages that lack one — which Acrobat then rejects as a zero-size page
        // ("dimensions exceed the limit, content may be truncated").
        var media = ReadInheritedRect(page, "/MediaBox");
        double x1 = 0, y1 = 0, x2 = page.Width.Point, y2 = page.Height.Point;
        if (media is { } m)
        {
            x1 = Math.Min(m[0], m[2]); y1 = Math.Min(m[1], m[3]);
            x2 = Math.Max(m[0], m[2]); y2 = Math.Max(m[1], m[3]);
        }

        if (ReadInheritedRect(page, "/CropBox") is { } c)
        {
            double cx1 = Math.Min(c[0], c[2]), cy1 = Math.Min(c[1], c[3]);
            double cx2 = Math.Max(c[0], c[2]), cy2 = Math.Max(c[1], c[3]);
            if (cx2 - cx1 > 0 && cy2 - cy1 > 0) // ignore an absent/degenerate crop box
            {
                x1 = Math.Max(x1, cx1); y1 = Math.Max(y1, cy1);
                x2 = Math.Min(x2, cx2); y2 = Math.Min(y2, cy2);
            }
        }

        return (x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Reads a 4-number rectangle from a page or its ancestors via the raw dictionary (no getter side effects).</summary>
    private static double[]? ReadInheritedRect(PdfDictionary? node, string key)
    {
        for (var guard = 0; node is not null && guard < 32; guard++)
        {
            if (node.Elements.GetArray(key) is { Elements.Count: 4 } a)
                return [a.Elements.GetReal(0), a.Elements.GetReal(1), a.Elements.GetReal(2), a.Elements.GetReal(3)];
            node = node.Elements.GetDictionary("/Parent");
        }
        return null;
    }

    /// <summary>
    /// Maps the upright rendered-image space (what OCR measured, bottom-left origin) into the page's
    /// unrotated content space for a given <paramref name="rotate"/> (/Rotate, a multiple of 90). The render
    /// box origin (<paramref name="ox"/>, <paramref name="oy"/>) is folded into the translation so a cropped
    /// or offset page still lines up. Returns the upright image dimensions and the PDF <c>cm</c> operands.
    /// </summary>
    private static (double DisplayW, double DisplayH, string Cm) RotationTransform(
        int rotate, double ox, double oy, double boxW, double boxH)
    {
        var r = ((rotate % 360) + 360) % 360;
        return r switch
        {
            90  => (boxH, boxW, $"0 1 -1 0 {F(boxW + ox)} {F(oy)}"),
            180 => (boxW, boxH, $"-1 0 0 -1 {F(boxW + ox)} {F(boxH + oy)}"),
            270 => (boxH, boxW, $"0 -1 1 0 {F(ox)} {F(boxH + oy)}"),
            _   => (boxW, boxH, $"1 0 0 1 {F(ox)} {F(oy)}"),
        };
    }

    /// <summary>
    /// Adds a non-embedded standard <c>Helvetica</c> font to the page resources under <paramref name="name"/>
    /// when absent. With render mode 3 the glyph shapes are never painted, so the base font only needs to
    /// carry the character codes for text extraction; WinAnsi covers the Latin / Italian range.
    /// </summary>
    private static void EnsureStandardFont(PdfDocument doc, PdfPage page, string name)
    {
        // A scanned page carries its image XObject in its own /Resources, so creating one here never
        // shadows inherited resources in practice; otherwise merge into the existing dictionaries.
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null) { resources = new PdfDictionary(doc); page.Elements["/Resources"] = resources; }
        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null) { fonts = new PdfDictionary(doc); resources.Elements["/Font"] = fonts; }
        if (fonts.Elements.ContainsKey("/" + name)) return;

        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(font);
        fonts.Elements["/" + name] = font.Reference;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Maps common typographic glyphs into the WinAnsi range and escapes PDF string delimiters.</summary>
    private static string EscapePdfText(string text)
    {
        var normalized = text
            .Replace('’', '\'').Replace('‘', '\'')   // ’ ‘ -> '
            .Replace('“', '"').Replace('”', '"')     // “ ” -> "
            .Replace('–', '-').Replace('—', '-')     // – — -> -
            .Replace(' ', ' ');                           // nbsp -> space
        return normalized.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    // WinAnsi (CP1252) glyphs that live in the 0x80–0x9F byte band but carry Unicode code points > 0xFF.
    // These must be byte-mapped explicitly: a plain Latin-1 encode turns them into '?' (e.g. € -> '?'), which
    // would break search/copy of amounts like "€1.250". Most are already folded to ASCII by EscapePdfText;
    // the Euro sign is the common one that reaches here intact.
    private static readonly Dictionary<char, byte> WinAnsiHighBytes = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85,
        ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A,
        ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92,
        ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C,
        ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>
    /// Encodes the content stream as single-byte WinAnsi (CP1252), matching the OCR font's <c>/WinAnsiEncoding</c>.
    /// Bytes 0x00–0xFF map directly (operators are ASCII; the Latin-1 accented à–ÿ range coincides with WinAnsi),
    /// the 0x80–0x9F glyphs (€, etc.) are mapped explicitly so they round-trip for search/copy, and anything
    /// outside WinAnsi degrades to '?'. (.NET has no built-in CP1252 encoder without an extra package.)
    /// </summary>
    private static byte[] EncodeWinAnsi(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c <= 0xFF) bytes[i] = (byte)c;
            else if (WinAnsiHighBytes.TryGetValue(c, out var b)) bytes[i] = b;
            else bytes[i] = (byte)'?';
        }
        return bytes;
    }
}
