using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PDFtoImage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Secure redaction via rasterisation: each affected page is rendered to a high-DPI PNG by PDFium,
/// black filled rectangles are drawn over the sensitive regions with SkiaSharp, and the page is
/// replaced in the output PDF with the processed image — removing all underlying vector/text content.
/// </summary>
internal sealed class PdfRedactionService : IPdfRedactionService
{
    private readonly IPdfRenderer _renderer;

    public PdfRedactionService(IPdfRenderer renderer) => _renderer = renderer;

    public async Task<byte[]> RedactAsync(
        byte[] pdf,
        IReadOnlyList<(int PageIndex, PdfRect Rect)> regions,
        int renderDpi = 300,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderDpi);

        if (regions.Count == 0) return pdf;

        // Group rectangles by page index and validate.
        var byPage = regions
            .GroupBy(r => r.PageIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PdfRect>)g.Select(r => r.Rect).ToList());

        var pageCount = Conversion.GetPageCount(pdf);
        foreach (var idx in byPage.Keys)
        {
            if ((uint)idx >= (uint)pageCount)
                throw new ArgumentOutOfRangeException(nameof(regions),
                    $"Page index {idx} is out of range (0..{pageCount - 1}).");
        }

        // Render each affected page and burn black rectangles.
        var processedImages = new Dictionary<int, byte[]>(byPage.Count);
        foreach (var (pageIdx, rects) in byPage)
        {
            ct.ThrowIfCancellationRequested();
            var pngBytes = await _renderer.RenderPageToPngAsync(pdf, pageIdx, renderDpi, ct);
            processedImages[pageIdx] = BurnRegions(pngBytes, rects);
        }

        // Build the output PDF: copy unchanged pages, replace redacted pages with image pages.
        return await Task.Run(() => BuildRedactedPdf(pdf, processedImages, ct), ct);
    }

    private static byte[] BurnRegions(byte[] pngBytes, IReadOnlyList<PdfRect> rects)
    {
        using var bitmap = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidOperationException("Failed to decode rendered page PNG.");
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };

        foreach (var rect in rects)
        {
            canvas.DrawRect(
                SKRect.Create(
                    (float)(rect.Left * bitmap.Width),
                    (float)(rect.Top * bitmap.Height),
                    (float)(rect.Width * bitmap.Width),
                    (float)(rect.Height * bitmap.Height)),
                paint);
        }

        canvas.Flush();
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] BuildRedactedPdf(
        byte[] originalPdf,
        Dictionary<int, byte[]> processedImages,
        CancellationToken ct)
    {
        using var inputStream = new MemoryStream(originalPdf, writable: false);
        using var sourceDoc = PdfReader.Open(inputStream, PdfDocumentOpenMode.Import);
        using var outputDoc = new PdfDocument();

        for (var i = 0; i < sourceDoc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!processedImages.TryGetValue(i, out var pngBytes))
            {
                outputDoc.AddPage(sourceDoc.Pages[i]);
                continue;
            }

            // Replace page with the rasterised (text-free) image.
            var srcPage = sourceDoc.Pages[i];
            var newPage = outputDoc.AddPage();
            newPage.Width = srcPage.Width;
            newPage.Height = srcPage.Height;

            using var gfx = XGraphics.FromPdfPage(newPage);
            using var imgStream = new MemoryStream(pngBytes);
            var xImg = XImage.FromStream(imgStream);
            try
            {
                gfx.DrawImage(xImg, new XRect(0, 0, newPage.Width.Point, newPage.Height.Point));
            }
            finally
            {
                xImg.Dispose();
            }
        }

        using var output = new MemoryStream();
        outputDoc.Save(output);
        return output.ToArray();
    }
}
