using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Hisui.Pdf.Core.Services;

internal sealed class PdfWordExporter : IPdfWordExporter
{
    private const long EmuPerPx = 9525;
    private const long MaxImageWidthEmu = 6 * 914400; // 6 inches: fits an A4 page with default margins

    private readonly IPdfRenderer _renderer;
    private readonly IPdfTextExtractor _text;
    private readonly IPdfImageExtractor _images;

    public PdfWordExporter(IPdfRenderer renderer, IPdfTextExtractor text, IPdfImageExtractor images)
    {
        _renderer = renderer;
        _text = text;
        _images = images;
    }

    public async Task<byte[]> ExportAsync(byte[] pdf, CancellationToken ct = default)
    {
        var pageCount = await _renderer.GetPageCountAsync(pdf, ct);

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            for (var page = 0; page < pageCount; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (page > 0) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

                var words = await _text.ExtractWordsAsync(pdf, page, ct);
                foreach (var paragraph in BuildParagraphs(words)) body.Append(paragraph);

                foreach (var image in await _images.ExtractImagesAsync(pdf, page, ct))
                    if (TryBuildImage(main, image) is { } drawing)
                        body.Append(new Paragraph(new Run(drawing)));
            }
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>Groups words (reading order) into lines, and lines into paragraphs.</summary>
    private static IEnumerable<Paragraph> BuildParagraphs(IReadOnlyList<TextWord> words)
    {
        var ordered = ReadingOrder.Sort(words, w => w.BoundingBox);

        var lines = new List<List<TextWord>>();
        foreach (var w in ordered)
        {
            var current = lines.Count > 0 ? lines[^1] : null;
            if (current is not null && SameLine(current[^1], w)) current.Add(w);
            else lines.Add([w]);
        }

        Paragraph? paragraph = null;
        TextWord? prevFirst = null;
        foreach (var line in lines)
        {
            var first = line[0];
            var height = Math.Max(first.BoundingBox.Bottom - first.BoundingBox.Top, 0.001);
            var newParagraph = paragraph is null || prevFirst is null
                || first.BoundingBox.Top - prevFirst.BoundingBox.Top > height * 1.6 // blank gap
                || first.BoundingBox.Top < prevFirst.BoundingBox.Top;                // jumped back up (next column)
            if (newParagraph)
            {
                if (paragraph is not null) yield return paragraph;
                paragraph = new Paragraph();
            }
            else paragraph!.Append(new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve }));

            var size = (int)Math.Round(first.FontSizePoints * 2); // half-points
            paragraph!.Append(new Run(
                new RunProperties(new FontSize { Val = Math.Clamp(size, 12, 144).ToString() }),
                new Text(string.Join(' ', line.Select(w => w.Text))) { Space = SpaceProcessingModeValues.Preserve }));
            prevFirst = first;
        }
        if (paragraph is not null) yield return paragraph;
    }

    private static bool SameLine(TextWord prev, TextWord next)
    {
        var h = Math.Max(prev.BoundingBox.Bottom - prev.BoundingBox.Top, 0.001);
        var dy = Math.Abs((next.BoundingBox.Top + next.BoundingBox.Bottom) / 2 - (prev.BoundingBox.Top + prev.BoundingBox.Bottom) / 2);
        return dy < h * 0.6 && next.BoundingBox.Left >= prev.BoundingBox.Left;
    }

    private static Drawing? TryBuildImage(MainDocumentPart main, ExtractedImage image)
    {
        ImagePart part;
        if (image.Format.Equals("PNG", StringComparison.OrdinalIgnoreCase)) part = main.AddImagePart(ImagePartType.Png);
        else if (image.Format.Equals("JPEG", StringComparison.OrdinalIgnoreCase)) part = main.AddImagePart(ImagePartType.Jpeg);
        else return null; // undecodable raw stream

        using (var s = new MemoryStream(image.Bytes)) part.FeedData(s);

        long cx = Math.Max(image.WidthPx, 1) * EmuPerPx, cy = Math.Max(image.HeightPx, 1) * EmuPerPx;
        if (cx > MaxImageWidthEmu) { cy = cy * MaxImageWidthEmu / cx; cx = MaxImageWidthEmu; }
        var id = (uint)(main.ImageParts.Count() + 1);

        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = id, Name = $"Image {id}" },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = id, Name = $"image{id}" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
    }
}
