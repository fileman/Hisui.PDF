using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using UglyToad.PdfPig;

namespace Hisui.Pdf.Core.Services;

/// <summary>Text extraction and search backed by PdfPig (word-granularity with bounding boxes).</summary>
internal sealed class PdfTextExtractor : IPdfTextExtractor
{
    public Task<string> ExtractTextAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var doc = PdfDocument.Open(pdf);

            if (pageIndex is int idx)
            {
                ValidatePage(idx, doc.NumberOfPages);
                return doc.GetPage(idx + 1).Text; // PdfPig is 1-based
            }

            var sb = new System.Text.StringBuilder();
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (sb.Length > 0) sb.Append('\f'); // form-feed page separator
                sb.Append(page.Text);
            }
            return sb.ToString();
        }, ct);
    }

    public Task<IReadOnlyList<TextWord>> ExtractWordsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<TextWord>>(() =>
        {
            using var doc = PdfDocument.Open(pdf);
            ValidatePage(pageIndex, doc.NumberOfPages);

            var page = doc.GetPage(pageIndex + 1);
            var result = new List<TextWord>();
            foreach (var word in page.GetWords())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new TextWord
                {
                    Text = word.Text,
                    PageIndex = pageIndex,
                    BoundingBox = ToBoundingBox(word.BoundingBox, page.Width, page.Height),
                    FontSizePoints = word.Letters.FirstOrDefault()?.FontSize ?? 12,
                });
            }
            return result;
        }, ct);
    }

    public Task<IReadOnlyList<TextSearchResult>> SearchAsync(
        byte[] pdf, string query,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(query);

        return Task.Run<IReadOnlyList<TextSearchResult>>(() =>
        {
            using var doc = PdfDocument.Open(pdf);
            var results = new List<TextSearchResult>();

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var pageIdx = page.Number - 1;

                foreach (var word in page.GetWords())
                {
                    if (word.Text.Contains(query, comparison))
                    {
                        results.Add(new TextSearchResult
                        {
                            MatchedText = word.Text,
                            PageIndex = pageIdx,
                            BoundingBox = ToBoundingBox(word.BoundingBox, page.Width, page.Height),
                        });
                    }
                }
            }
            return results;
        }, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void ValidatePage(int pageIndex, int pageCount)
    {
        if ((uint)pageIndex >= (uint)pageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex),
                $"Page {pageIndex} does not exist (count = {pageCount}).");
    }

    /// <summary>
    /// Converts a PdfPig rectangle (PDF coords: origin bottom-left, Y up) to a normalized
    /// PdfRect (screen coords: origin top-left, Y down).
    /// </summary>
    private static PdfRect ToBoundingBox(UglyToad.PdfPig.Core.PdfRectangle bb, double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
            return new PdfRect(0, 0, 0, 0);

        return new PdfRect(
            Left:   bb.Left / pageWidth,
            Top:    (pageHeight - bb.Top) / pageHeight,
            Right:  bb.Right / pageWidth,
            Bottom: (pageHeight - bb.Bottom) / pageHeight);
    }
}
