using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using UglyToad.PdfPig;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Extracts raster images from PDF pages via PdfPig.
/// Images are converted to PNG where possible; raw bytes are returned otherwise.
/// </summary>
internal sealed class PdfImageExtractor : IPdfImageExtractor
{
    public Task<IReadOnlyList<ExtractedImage>> ExtractImagesAsync(
        byte[] pdf, int? pageIndex = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<ExtractedImage>>(() =>
        {
            using var doc = PdfDocument.Open(pdf);

            if (pageIndex is int idx)
            {
                if ((uint)idx >= (uint)doc.NumberOfPages)
                    throw new ArgumentOutOfRangeException(nameof(pageIndex),
                        $"Page {idx} does not exist (count = {doc.NumberOfPages}).");

                return ExtractFromPage(doc.GetPage(idx + 1), idx, ct);
            }

            var all = new List<ExtractedImage>();
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                all.AddRange(ExtractFromPage(page, page.Number - 1, ct));
            }
            return all;
        }, ct);
    }

    private static List<ExtractedImage> ExtractFromPage(
        UglyToad.PdfPig.Content.Page page, int pageIdx, CancellationToken ct)
    {
        var results = new List<ExtractedImage>();

        foreach (var image in page.GetImages())
        {
            ct.ThrowIfCancellationRequested();

            byte[]? bytes = null;
            string format;

            if (image.TryGetPng(out var pngBytes))
            {
                bytes = pngBytes;
                format = "PNG";
            }
            else
            {
                bytes = [.. image.RawBytes];
                format = "raw";
            }

            if (bytes is null || bytes.Length == 0) continue;

            results.Add(new ExtractedImage
            {
                PageIndex = pageIdx,
                Bytes = bytes,
                Format = format,
                WidthPx = image.WidthInSamples,
                HeightPx = image.HeightInSamples,
                BoundingBox = ToBoundingBox(image.BoundingBox, page.Width, page.Height),
            });
        }

        return results;
    }

    private static PdfRect ToBoundingBox(UglyToad.PdfPig.Core.PdfRectangle bb, double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return new PdfRect(0, 0, 0, 0);

        return new PdfRect(
            Left:   bb.Left / pageWidth,
            Top:    (pageHeight - bb.Top) / pageHeight,
            Right:  bb.Right / pageWidth,
            Bottom: (pageHeight - bb.Bottom) / pageHeight);
    }
}
