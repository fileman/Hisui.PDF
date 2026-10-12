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
            var (flat, rotations) = PageGeometry.Unrotate(pdf);
            using var doc = PdfDocument.Open(flat);
            ValidatePage(pageIndex, doc.NumberOfPages);

            var page = doc.GetPage(pageIndex + 1);
            var turn = RotationOf(rotations, pageIndex);
            var result = new List<TextWord>();
            foreach (var word in page.GetWords())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new TextWord
                {
                    Text = word.Text,
                    PageIndex = pageIndex,
                    BoundingBox = ToBoundingBox(word.BoundingBox, page.Width, page.Height).RotateClockwise(turn),
                    FontSizePoints = word.Letters.FirstOrDefault()?.FontSize ?? 12,
                });
            }
            return result;
        }, ct);
    }

    public Task<IReadOnlyList<IReadOnlyList<TextWord>>> ExtractAllWordsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<IReadOnlyList<TextWord>>>(() =>
        {
            var (flat, rotations) = PageGeometry.Unrotate(pdf);
            using var doc = PdfDocument.Open(flat);
            var pages = new List<IReadOnlyList<TextWord>>();
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var turn = RotationOf(rotations, page.Number - 1);
                var horizontal = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.TextOrientation == UglyToad.PdfPig.Content.TextOrientation.Horizontal)
                    .Select(w => new TextWord
                    {
                        Text = w.Text,
                        PageIndex = page.Number - 1,
                        BoundingBox = ToBoundingBox(w.BoundingBox, page.Width, page.Height).RotateClockwise(turn),
                        FontSizePoints = w.Letters.FirstOrDefault()?.FontSize ?? 12,
                    });
                var vertical = VerticalLines(page.Letters).Select(line => new TextWord
                {
                    Text = line.Text,
                    PageIndex = page.Number - 1,
                    BoundingBox = ToBoundingBox(line.Box, page.Width, page.Height).RotateClockwise(turn),
                    FontSizePoints = line.FontSize,
                    IsHorizontal = false,
                });
                pages.Add(horizontal.Concat(vertical).ToList());
            }
            return pages;
        }, ct);
    }

    /// <summary>
    /// Vertical text (e.g. rotated column headers) as one entry per vertical line. PdfPig's word grouping assumes
    /// horizontal text and interleaves neighbouring rotated lines, so the lines are rebuilt from the letters.
    /// </summary>
    private static IEnumerable<(string Text, UglyToad.PdfPig.Core.PdfRectangle Box, double FontSize)> VerticalLines(IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters)
    {
        var index = new Dictionary<UglyToad.PdfPig.Content.Letter, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < letters.Count; i++) index[letters[i]] = i;
        var rotated = letters.Where(l => l.TextOrientation is UglyToad.PdfPig.Content.TextOrientation.Rotate90 or UglyToad.PdfPig.Content.TextOrientation.Rotate270
                                         && !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var lines = new List<List<UglyToad.PdfPig.Content.Letter>>();
        foreach (var l in rotated.OrderBy(l => (l.BoundingBox.Left + l.BoundingBox.Right) / 2))
        {
            var cx = (l.BoundingBox.Left + l.BoundingBox.Right) / 2;
            var line = lines.LastOrDefault();
            var lineX = line is null ? double.NaN : line.Average(x => (x.BoundingBox.Left + x.BoundingBox.Right) / 2);
            if (line is null || Math.Abs(cx - lineX) > Math.Max(1, l.FontSize) * 0.45) lines.Add(line = []);
            line.Add(l);
        }
        foreach (var line in lines)
        {
            // Content order is reading order; a space goes where the pen jumps (baseline end → next start).
            var ordered = line.OrderBy(l => index[l]).ToList();
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                {
                    var (a, b) = (ordered[i - 1].EndBaseLine, ordered[i].StartBaseLine);
                    if (Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) > ordered[i].FontSize * 0.2) sb.Append(' ');
                }
                sb.Append(ordered[i].Value);
            }
            var box = new UglyToad.PdfPig.Core.PdfRectangle(line.Min(l => l.BoundingBox.Left), line.Min(l => l.BoundingBox.Bottom),
                line.Max(l => l.BoundingBox.Right), line.Max(l => l.BoundingBox.Top));
            yield return (sb.ToString().Trim(), box, line[0].FontSize);
        }
    }

    public async Task<IReadOnlyList<TextSearchResult>> SearchPatternAsync(byte[] pdf, System.Text.RegularExpressions.Regex pattern, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var pages = await ExtractAllWordsAsync(pdf, ct);
        return pages.SelectMany((words, i) => FindPattern(words.Select(w => (w.Text, w.BoundingBox)).ToList(), i, pattern)).ToList();
    }

    /// <summary>Regex counterpart of <see cref="FindMatches"/>: matches over the space-joined words, mapped back to word boxes.</summary>
    internal static IEnumerable<TextSearchResult> FindPattern(IReadOnlyList<(string Text, PdfRect Box)> words, int pageIndex, System.Text.RegularExpressions.Regex pattern)
    {
        var sb = new System.Text.StringBuilder();
        var spans = new List<(int Start, int End, PdfRect Box)>(words.Count);
        foreach (var (text, box) in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            spans.Add((sb.Length, sb.Length + text.Length, box));
            sb.Append(text);
        }
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(sb.ToString()))
        {
            if (m.Length == 0) continue;
            var (start, end) = (m.Index, m.Index + m.Length);
            var boxes = spans.Where(s => s.Start < end && s.End > start).Select(s => s.Box).ToList();
            if (boxes.Count > 0) yield return new TextSearchResult { MatchedText = m.Value, PageIndex = pageIndex, Boxes = boxes };
        }
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
            var (flat, rotations) = PageGeometry.Unrotate(pdf);
            using var doc = PdfDocument.Open(flat);
            var results = new List<TextSearchResult>();

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var turn = RotationOf(rotations, page.Number - 1);
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrEmpty(w.Text))
                    .Select(w => (w.Text, Box: ToBoundingBox(w.BoundingBox, page.Width, page.Height).RotateClockwise(turn)))
                    .ToList();
                results.AddRange(FindMatches(words, page.Number - 1, needle, options));
            }
            return results;
        }, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int RotationOf(int[] rotations, int pageIndex) =>
        (uint)pageIndex < (uint)rotations.Length ? rotations[pageIndex] : 0;

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
