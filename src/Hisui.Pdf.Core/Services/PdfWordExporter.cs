using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// PDF → .docx. Digital pages use their text layer; scanned pages are rebuilt from OCR, with the logos,
/// signatures and stamps around the text cropped from the page as pictures. Every page becomes a section
/// with the PDF's page size and margins from the content bounds; paragraphs keep alignment, indents, spacing,
/// font size, bold, colour and links; pictures are placed in the flow at their vertical position.
/// </summary>
internal sealed partial class PdfWordExporter : IPdfWordExporter
{
    private const int FigureDpi = 200;
    private const int StrokeDpi = 300;              // stem widths need the finer raster
    private const double BoldStrokeRatio = 1.4;     // stems this much thicker than the page's typical text are bold
    private const double WeakBoldStrokeRatio = 1.15; // ...and this much, next to a bold word, continue the bold run
    private const float MinOcrConfidence = 40;   // below this an OCR "word" is dust, a seal or a scribble
    private const long EmuPerPoint = 12700;
    private const double MinMarginPt = 0.4 * 72, MaxMarginPt = 1.2 * 72;

    private readonly IPdfRenderer _renderer;
    private readonly IPdfTextExtractor _text;
    private readonly IPdfImageExtractor _images;
    private readonly IPdfOcrService? _ocr;

    public PdfWordExporter(IPdfRenderer renderer, IPdfTextExtractor text, IPdfImageExtractor images, IPdfOcrService? ocr = null)
    {
        _renderer = renderer;
        _text = text;
        _images = images;
        _ocr = ocr;
    }

    public async Task<byte[]> ExportAsync(byte[] pdf, CancellationToken ct = default) =>
        (await ExportAsync(pdf, null, ct).ConfigureAwait(false)).Docx;

    public async Task<WordExportResult> ExportAsync(byte[] pdf, WordExportOptions? options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new WordExportOptions();

        var sizes = await _renderer.GetPageSizesAsync(pdf, ct).ConfigureAwait(false);
        var allWords = await _text.ExtractAllWordsAsync(pdf, ct).ConfigureAwait(false);
        var (ocr, ocrError) = await RecognizeScansAsync(pdf, options, ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            var body = new Body();
            main.Document = new Document(body);
            var writer = new DocWriter(main);

            for (var page = 0; page < sizes.Count; page++)
            {
                ct.ThrowIfCancellationRequested();
                var words = page < allWords.Count ? allWords[page] : [];
                var content = ocr.TryGetValue(page, out var recognized)
                    ? await ScannedPageAsync(pdf, page, words, recognized, sizes[page], options.Ocr, ct).ConfigureAwait(false)
                    : await DigitalPageAsync(pdf, page, words, ct).ConfigureAwait(false);

                var layout = PageLayout.From(sizes[page], content);
                var paragraphs = BuildPage(writer, content, layout);
                if (paragraphs.Count == 0) paragraphs.Add(new Paragraph(new ParagraphProperties()));

                // One section per page: it carries the page size and margins and starts the next page.
                if (page < sizes.Count - 1)
                {
                    var last = paragraphs[^1];
                    (last.ParagraphProperties ??= new ParagraphProperties()).Append(layout.SectionProperties());
                }
                foreach (var p in paragraphs) body.Append(p);
                if (page == sizes.Count - 1) body.Append(layout.SectionProperties());
            }
            main.Document.Save();
        }
        return new WordExportResult(ms.ToArray(), [.. ocr.Keys.Order()], ocrError);
    }

    private async Task<(Dictionary<int, OcrPageResult> Pages, string? Error)> RecognizeScansAsync(
        byte[] pdf, WordExportOptions options, CancellationToken ct)
    {
        if (!options.UseOcr || _ocr is null) return (new(), null);
        try
        {
            var ocrOptions = (options.Ocr ?? new OcrOptions()) with { OnlyScannedPages = true };
            var result = await _ocr.RecognizeAsync(pdf, ocrOptions, ct).ConfigureAwait(false);
            return (result.Pages.ToDictionary(p => p.PageIndex), null);
        }
        catch (OcrUnavailableException ex)
        {
            return (new(), ex.Message);
        }
    }

    // ── page content ─────────────────────────────────────────────────────────

    private sealed record Picture(PdfRect Box, byte[] Bytes, bool IsJpeg);

    private sealed record PageContent(IReadOnlyList<TextWord> Words, IReadOnlyList<Picture> Pictures);

    /// <summary>
    /// A scanned page: OCR words for the text, the text layer's rotated lines (margin stamps) as they are,
    /// and the remaining ink (logo, signature, seal) cropped from the render. The scan strips themselves are dropped.
    /// </summary>
    private async Task<PageContent> ScannedPageAsync(byte[] pdf, int page, IReadOnlyList<TextWord> textLayer,
        OcrPageResult recognized, PdfPageSize size, OcrOptions? ocrOptions, CancellationToken ct)
    {
        var rotated = textLayer.Where(w => !w.IsHorizontal).ToList();
        var candidates = recognized.Words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && !rotated.Any(r => Overlaps(r.BoundingBox, w.BoundingBox)))
            .ToList();
        var lineHeight = TypicalHeight(candidates);
        bool IsText(OcrWord w) => IsPlausibleText(w, lineHeight);

        var render = await _renderer.RenderPageAsync(pdf, page, FigureDpi, ct).ConfigureAwait(false);
        var (figures, absorbed) = await Task.Run(
            () => ScanFigureDetector.Detect(render, candidates, IsText, rotated.Select(w => w.BoundingBox)), ct).ConfigureAwait(false);

        var kept = new List<OcrWord>();
        var fused = new List<PdfRect>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (absorbed.Contains(i)) continue;
            var w = candidates[i];
            if (IsText(w)) kept.Add(w);
            else if (w.BoundingBox.Height > MultiLineFactor * lineHeight && !figures.Any(f => Overlaps(f.Box, w.BoundingBox)))
                fused.Add(w.BoundingBox);
        }
        var text = kept.ToList();
        foreach (var box in fused)
            kept.AddRange(await ReadAgainAsync(pdf, page, box, text, lineHeight, ocrOptions, ct).ConfigureAwait(false));

        var sizes = OcrFontSizes(render, kept, size);
        var fine = await _renderer.RenderPageAsync(pdf, page, StrokeDpi, ct).ConfigureAwait(false);
        var bold = BoldFlags(fine, kept, sizes);
        var words = new List<TextWord>(rotated);
        for (var i = 0; i < kept.Count; i++)
            words.Add(new TextWord
            {
                Text = kept[i].Text,
                PageIndex = page,
                BoundingBox = kept[i].BoundingBox,
                FontSizePoints = sizes[i],
                IsBold = bold[i],
            });
        return new PageContent(words, [.. figures.Select(f => new Picture(f.Box, f.Png, false))]);
    }

    private const double MultiLineFactor = 1.6; // a "word" taller than this many lines is a fused block

    private static double TypicalHeight(IEnumerable<OcrWord> words)
    {
        var heights = words.Where(w => w.Confidence >= ScanFigureDetector.TrustedConfidence && w.Text.Length >= 3)
            .Select(w => w.BoundingBox.Height).Order().ToList();
        return heights.Count > 0 ? heights[heights.Count / 2] : 0.012;
    }

    /// <summary>
    /// Whether an OCR word is text worth keeping. Tesseract gives low confidence to perfectly good words with
    /// apostrophes, underlines or URLs, so a low score alone does not disqualify a word that is one line tall
    /// and mostly letters and digits; specks and blocks spanning several lines are not text.
    /// </summary>
    private static bool IsPlausibleText(OcrWord w, double lineHeight)
    {
        if (w.Confidence >= MinOcrConfidence) return true;
        if (w.Text.Length < 2 || w.BoundingBox.Height > MultiLineFactor * lineHeight) return false;
        return w.Text.Count(char.IsLetterOrDigit) >= 0.6 * w.Text.Length;
    }

    /// <summary>
    /// Re-reads a block that Tesseract fused into one tall garbage "word" (typically a column of list numbers).
    /// Tesseract reads bare numbers in a narrow column badly, so each row is re-read together with the first word
    /// beside it ("2. Housing"); what precedes that word is the marker. Rows with nothing beside them are dropped.
    /// </summary>
    private async Task<IEnumerable<OcrWord>> ReadAgainAsync(byte[] pdf, int page, PdfRect box, List<OcrWord> text,
        double lineHeight, OcrOptions? options, CancellationToken ct)
    {
        if (_ocr is null) return [];

        static double Centre(OcrWord w) => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
        var beside = text.Where(w => Centre(w) > box.Top && Centre(w) < box.Bottom && w.BoundingBox.Left >= box.Right)
            .OrderBy(Centre).ToList();

        // The first word of each row beside the block.
        var firsts = new List<OcrWord>();
        foreach (var row in beside.GroupBy(w => beside.First(o => Math.Abs(Centre(o) - Centre(w)) < 0.6 * lineHeight)))
            firsts.Add(row.MinBy(w => w.BoundingBox.Left)!);

        var markers = new string?[firsts.Count];
        for (var k = 0; k < firsts.Count; k++)
        {
            var first = firsts[k];
            var row = first.BoundingBox;
            var region = new PdfRect(Math.Max(0, box.Left - 0.02), Math.Max(0, row.Top - 0.5 * lineHeight),
                Math.Min(1, row.Right + 0.01), Math.Min(1, row.Bottom + 0.5 * lineHeight));
            string read;
            try
            {
                read = await _ocr.RecognizeRegionAsync(pdf, page, region, options, ct).ConfigureAwait(false);
            }
            catch (OcrUnavailableException)
            {
                return [];
            }

            var tokens = read.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var marker = string.Join(' ', tokens[..^1]);
            if (marker.Any(char.IsLetterOrDigit) && marker.Length <= 6) markers[k] = marker;
        }

        // A numbered list with one number misread: "2." … "4." → "3.".
        for (var k = 1; k < markers.Length - 1; k++)
            if (markers[k] is null && Numbered(markers[k - 1]) is { } before && Numbered(markers[k + 1]) is { } after
                && after.N == before.N + 2 && after.Suffix == before.Suffix)
                markers[k] = (before.N + 1).ToString(CultureInfo.InvariantCulture) + before.Suffix;

        var result = new List<OcrWord>();
        for (var k = 0; k < firsts.Count; k++)
        {
            if (markers[k] is not { } marker) continue;
            var row = firsts[k].BoundingBox;
            result.Add(new OcrWord
            {
                Text = marker,
                BoundingBox = new PdfRect(box.Left, row.Top, Math.Min(box.Right, row.Left - 0.002), row.Bottom),
                Confidence = MinOcrConfidence,
            });
        }
        return result;
    }

    private static (int N, string Suffix)? Numbered(string? marker)
    {
        if (marker is null) return null;
        var digits = new string([.. marker.TakeWhile(char.IsDigit)]);
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (n, marker[digits.Length..])
            : null;
    }

    /// <summary>
    /// Font sizes for OCR words, from the height of their ink without any underline. OCR boxes are tight around
    /// the glyphs, roughly cap/x-height plus descenders ≈ 0.72 em; sizes within 30% of the page's typical
    /// size snap to it, so body text does not jitter (headings are usually a third larger or more).
    /// </summary>
    private static List<double> OcrFontSizes(RenderedPage render, List<OcrWord> words, PdfPageSize size)
    {
        var raw = words.Select(w => ScanFigureDetector.InkHeight(render, w.BoundingBox) * size.Height / 0.72).ToList();
        var sample = words.Select((w, i) => (w, i)).Where(x => x.w.Text.Length >= 3).Select(x => raw[x.i]).Order().ToList();
        var typical = sample.Count > 0 ? sample[sample.Count / 2] : 11;
        return [.. raw.Select(pt => Math.Round((Math.Abs(pt - typical) / typical < 0.3 ? typical : pt) * 2) / 2)];
    }

    /// <summary>
    /// Bold from the scan: stems clearly thicker than the page's typical text, relative to the font size (a large
    /// title has thicker stems without being bold). Clear cases seed bold; a neighbour on the same row that is
    /// somewhat thicker than usual, or sits between two bold words, joins the bold run (bold comes in phrases).
    /// </summary>
    private static List<bool> BoldFlags(RenderedPage render, List<OcrWord> words, List<double> sizes)
    {
        var ratio = words.Select((w, i) => ScanFigureDetector.StrokeWidth(render, w.BoundingBox) / Math.Max(sizes[i], 1)).ToList();
        var reference = ratio.Where((r, i) => r > 0 && words[i].Text.Length >= 3 && words[i].Confidence >= ScanFigureDetector.TrustedConfidence)
            .Order().ToList();
        var typical = reference.Count > 0 ? reference[reference.Count / 2] : 0;
        var bold = ratio.Select((r, i) => typical > 0 && r >= BoldStrokeRatio * typical && words[i].Text.Length >= 2).ToList();

        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < words.Count; i++)
            {
                if (bold[i] || typical <= 0) continue;
                var prev = i > 0 && bold[i - 1] && SameRow(words[i - 1], words[i]);
                var next = i < words.Count - 1 && bold[i + 1] && SameRow(words[i], words[i + 1]);
                if ((prev && next) || ((prev || next) && ratio[i] >= WeakBoldStrokeRatio * typical))
                {
                    bold[i] = true;
                    changed = true;
                }
            }
        } while (changed);
        return bold;
    }

    private static bool SameRow(OcrWord a, OcrWord b) =>
        Math.Abs((a.BoundingBox.Top + a.BoundingBox.Bottom) / 2 - (b.BoundingBox.Top + b.BoundingBox.Bottom) / 2)
        < 0.5 * Math.Max(a.BoundingBox.Height, b.BoundingBox.Height);

    /// <summary>A digital page: the text layer, and the embedded images with sliced scans glued back together.</summary>
    private async Task<PageContent> DigitalPageAsync(byte[] pdf, int page, IReadOnlyList<TextWord> words, CancellationToken ct)
    {
        var images = await _images.ExtractImagesAsync(pdf, page, ct).ConfigureAwait(false);
        var groups = GroupStrips(images.Where(i => i.BoundingBox.Width > 0.003 && i.BoundingBox.Height > 0.003));

        var pictures = new List<Picture>();
        RenderedPage? render = null;
        foreach (var group in groups)
        {
            var single = group.Count == 1 ? group[0] : null;
            if (single is { Format: "PNG" } || single is { Format: "JPEG" })
            {
                pictures.Add(new Picture(single.BoundingBox, single.Bytes, single.Format == "JPEG"));
                continue;
            }

            // Strips or an undecodable stream: crop the region from the rendered page instead.
            render ??= await _renderer.RenderPageAsync(pdf, page, FigureDpi, ct).ConfigureAwait(false);
            var box = Union(group.Select(g => g.BoundingBox));
            if (CropPng(render, box) is { } png) pictures.Add(new Picture(box, png, false));
        }
        return new PageContent(words, pictures);
    }

    /// <summary>
    /// Groups images that tile one picture: stacked with (almost) no gap and sharing their left and right edges.
    /// Scanners and PDF printers often slice a page image into horizontal bands.
    /// </summary>
    private static List<List<ExtractedImage>> GroupStrips(IEnumerable<ExtractedImage> images)
    {
        const double tolerance = 0.01;
        var groups = new List<(PdfRect Box, List<ExtractedImage> Items)>();
        foreach (var image in images.OrderBy(i => i.BoundingBox.Top))
        {
            var b = image.BoundingBox;
            var index = groups.FindIndex(g =>
                Math.Abs(g.Box.Left - b.Left) < tolerance && Math.Abs(g.Box.Right - b.Right) < tolerance
                && b.Top - g.Box.Bottom < tolerance && b.Top >= g.Box.Top);
            if (index < 0) groups.Add((b, [image]));
            else
            {
                var g = groups[index];
                g.Items.Add(image);
                groups[index] = (Union([g.Box, b]), g.Items);
            }
        }
        return [.. groups.Select(g => g.Items)];
    }

    private static byte[]? CropPng(RenderedPage render, PdfRect box)
    {
        int x0 = Math.Clamp((int)Math.Floor(box.Left * render.Width), 0, render.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(box.Top * render.Height), 0, render.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(box.Right * render.Width), x0 + 1, render.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(box.Bottom * render.Height), y0 + 1, render.Height);
        if (x1 - x0 < 2 || y1 - y0 < 2) return null;
        return PdfiumRenderer.EncodePng(ScanFigureDetector.Crop(render, x0, y0, x1 - x0, y1 - y0));
    }

    private static PdfRect Union(IEnumerable<PdfRect> rects)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var x in rects) { l = Math.Min(l, x.Left); t = Math.Min(t, x.Top); r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom); }
        return l > r ? default : new PdfRect(l, t, r, b);
    }

    private static bool Overlaps(PdfRect a, PdfRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    // ── page geometry ────────────────────────────────────────────────────────

    /// <summary>
    /// Page size in points, the box the content actually occupies (normalised) and the Word text area derived
    /// from it: margins follow the content, clamped to a sensible range.
    /// </summary>
    private sealed record PageLayout(double WidthPt, double HeightPt, PdfRect Content, PdfRect TextArea)
    {
        public static PageLayout From(PdfPageSize size, PageContent content)
        {
            double w = size.Width > 0 ? size.Width : 595, h = size.Height > 0 ? size.Height : 842;
            var boxes = content.Words.Where(x => x.IsHorizontal).Select(x => x.BoundingBox)
                .Concat(content.Pictures.Select(p => p.Box)).ToList();
            var c = boxes.Count > 0 ? Union(boxes) : new PdfRect(0.1, 0.1, 0.9, 0.9);

            double Margin(double fraction, double extent) => Math.Clamp(fraction * extent, MinMarginPt, MaxMarginPt) / extent;
            var text = new PdfRect(Margin(c.Left, w), Margin(c.Top, h), 1 - Margin(1 - c.Right, w), 1 - Margin(1 - c.Bottom, h));
            if (text.Width <= 0.2) text = new PdfRect(MinMarginPt / w, text.Top, 1 - MinMarginPt / w, text.Bottom);
            if (text.Height <= 0.2) text = new PdfRect(text.Left, MinMarginPt / h, text.Right, 1 - MinMarginPt / h);
            return new PageLayout(w, h, c, text);
        }

        public double TextWidthPt => TextArea.Width * WidthPt;
        public double TextHeightPt => TextArea.Height * HeightPt;

        /// <summary>Horizontal offset from the left margin, in twips (never negative).</summary>
        public int IndentTwips(double left) => Math.Max(0, (int)Math.Round((left - TextArea.Left) * WidthPt * 20));

        public SectionProperties SectionProperties()
        {
            var size = new PageSize { Width = (uint)Math.Round(WidthPt * 20), Height = (uint)Math.Round(HeightPt * 20) };
            if (WidthPt > HeightPt) size.Orient = PageOrientationValues.Landscape;
            uint Twips(double pt) => (uint)Math.Round(pt * 20);
            var top = Twips(TextArea.Top * HeightPt);
            var bottom = Twips((1 - TextArea.Bottom) * HeightPt);
            return new SectionProperties(size, new PageMargin
            {
                Top = (int)top,
                Bottom = (int)bottom,
                Left = Twips(TextArea.Left * WidthPt),
                Right = Twips((1 - TextArea.Right) * WidthPt),
                Header = Math.Min(360u, top),
                Footer = Math.Min(360u, bottom),
                Gutter = 0,
            });
        }
    }

    // ── document parts ───────────────────────────────────────────────────────

    /// <summary>Per-document state: unique drawing ids and the main part for images and link relationships.</summary>
    private sealed class DocWriter(MainDocumentPart main)
    {
        private uint _nextId = 1;
        public MainDocumentPart Main { get; } = main;
        public uint NextId() => _nextId++;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
            {
                Type = StyleValues.Paragraph, StyleId = "Normal", Default = true,
            },
            new Style(
                new StyleName { Val = "Hyperlink" },
                new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
            {
                Type = StyleValues.Character, StyleId = "Hyperlink",
            });
        part.Styles.Save();
    }

    private static Paragraph PictureParagraph(DocWriter writer, Picture picture, PageLayout layout)
    {
        var part = writer.Main.AddImagePart(picture.IsJpeg ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var s = new MemoryStream(picture.Bytes)) part.FeedData(s);

        // Placed size on the page, shrunk to fit the text area if needed.
        double wPt = Math.Max(picture.Box.Width * layout.WidthPt, 1), hPt = Math.Max(picture.Box.Height * layout.HeightPt, 1);
        var scale = Math.Min(1, Math.Min(layout.TextWidthPt / wPt, layout.TextHeightPt * 0.95 / hPt));
        wPt *= scale; hPt *= scale;
        long cx = (long)(wPt * EmuPerPoint), cy = (long)(hPt * EmuPerPoint);

        var indent = Math.Min(layout.IndentTwips(picture.Box.Left), (int)Math.Max(0, (layout.TextWidthPt - wPt) * 20));
        var id = writer.NextId();
        var drawing = new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = $"Picture {id}" },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = id, Name = $"picture{id}" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = writer.Main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0,
        });

        var props = new ParagraphProperties(new SpacingBetweenLines { Before = "0" });
        if (indent > 0) props.Append(new Indentation { Left = indent.ToString() });
        return new Paragraph(props, new Run(drawing));
    }
}
