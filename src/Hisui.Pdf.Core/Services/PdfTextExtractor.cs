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
        byte[] pdf, string query, TextSearchOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(query);
        options ??= new TextSearchOptions();

        var needle = CollapseWhitespace(query);
        if (needle.Length == 0) return Task.FromResult<IReadOnlyList<TextSearchResult>>([]);

        return Task.Run<IReadOnlyList<TextSearchResult>>(() =>
        {
            using var doc = PdfDocument.Open(pdf);
            var results = new List<TextSearchResult>();

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrEmpty(w.Text))
                    .Select(w => (w.Text, Box: ToBoundingBox(w.BoundingBox, page.Width, page.Height)))
                    .ToList();
                results.AddRange(FindMatches(words, page.Number - 1, needle, options));
            }
            return results;
        }, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Phrase-aware match over a page's words. The words are joined with single spaces into one searchable
    /// string (so a phrase matches across words and line breaks); each match maps back to the boxes of every
    /// word it overlaps. <paramref name="needle"/> must already be whitespace-collapsed and non-empty.
    /// Pure and PdfPig-free so the matching logic can be unit-tested directly.
    /// </summary>
    internal static IReadOnlyList<TextSearchResult> FindMatches(
        IReadOnlyList<(string Text, PdfRect Box)> words, int pageIndex, string needle, TextSearchOptions options)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var sb = new System.Text.StringBuilder();
        var spans = new List<(int Start, int End, PdfRect Box)>(words.Count);
        foreach (var (text, box) in words)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (sb.Length > 0) sb.Append(' ');
            var start = sb.Length;
            sb.Append(text);
            spans.Add((start, sb.Length, box));
        }

        var haystack = sb.ToString();
        var results = new List<TextSearchResult>();
        var from = 0;
        while (needle.Length > 0 && from <= haystack.Length - needle.Length)
        {
            var hit = haystack.IndexOf(needle, from, comparison);
            if (hit < 0) break;
            var end = hit + needle.Length;

            if (!options.WholeWord || IsWholeWord(haystack, hit, end))
            {
                var boxes = spans.Where(s => s.Start < end && s.End > hit).Select(s => s.Box).ToList();
                if (boxes.Count > 0)
                    results.Add(new TextSearchResult { MatchedText = haystack[hit..end], PageIndex = pageIndex, Boxes = boxes });
            }
            from = end; // non-overlapping matches, like a normal find-in-page
        }
        return results;
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c)) { if (!prevSpace) sb.Append(' '); prevSpace = true; }
            else { sb.Append(c); prevSpace = false; }
        }
        return sb.ToString();
    }

    private static bool IsWholeWord(string s, int start, int end)
    {
        var leftOk = start == 0 || !IsWordChar(s[start - 1]);
        var rightOk = end >= s.Length || !IsWordChar(s[end]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
