using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Native;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Text extraction and search backed by PDFium's text API (word-granularity with bounding boxes).
/// Reads go through the renderer's cached open document (<see cref="IPdfiumDocumentHost"/>), one page
/// per gate acquisition, so a long extraction never starves rendering.
/// </summary>
internal sealed class PdfTextExtractor : IPdfTextExtractor
{
    // Tests (and simple hosts) construct the extractor directly; they share one lazily created renderer
    // rather than each leaking a cache of pinned buffers.
    private static readonly Lazy<PdfiumRenderer> DefaultHost = new(() => new PdfiumRenderer());

    private readonly IPdfiumDocumentHost _host;

    public PdfTextExtractor() : this(DefaultHost.Value) { }

    public PdfTextExtractor(IPdfiumDocumentHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    public Task<string> ExtractTextAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            var count = PageCount(pdf);
            if (pageIndex is int idx)
            {
                ValidatePage(idx, count);
                return PageText(pdf, idx);
            }

            var sb = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i > 0) sb.Append('\f'); // form-feed page separator
                sb.Append(PageText(pdf, i));
            }
            return sb.ToString();
        }, ct);
    }

    public Task<IReadOnlyList<TextWord>> ExtractWordsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<TextWord>>(() =>
        {
            ValidatePage(pageIndex, PageCount(pdf));
            return WordsOf(ReadPage(pdf, pageIndex), pageIndex, splitVerticalIntoWords: true);
        }, ct);
    }

    public Task<IReadOnlyList<IReadOnlyList<TextWord>>> ExtractAllWordsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<IReadOnlyList<TextWord>>>(() =>
        {
            var count = PageCount(pdf);
            var pages = new List<IReadOnlyList<TextWord>>(count);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(WordsOf(ReadPage(pdf, i), i, splitVerticalIntoWords: false));
            }
            return pages;
        }, ct);
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
        var sb = new StringBuilder();
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
            var count = PageCount(pdf);
            var results = new List<TextSearchResult>();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Every word, whatever its orientation, in content order (rotated text stays searchable).
                var words = WordsOf(ReadPage(pdf, i), i, splitVerticalIntoWords: true)
                    .Select(w => (w.Text, Box: w.BoundingBox))
                    .ToList();
                results.AddRange(FindMatches(words, i, needle, options));
            }
            return results;
        }, ct);
    }

    // ── PDFium access ────────────────────────────────────────────────────────

    private int PageCount(byte[] pdf) => _host.WithDocument(pdf, static (_, count) => count);

    private string PageText(byte[] pdf, int pageIndex) => _host.WithDocument(pdf, (doc, _) =>
    {
        var (page, text) = LoadTextPage(doc, pageIndex);
        try
        {
            var chars = PdfiumNative.FPDFText_CountChars(text);
            if (chars <= 0) return string.Empty;
            var buffer = new char[chars + 1];
            int written;
            unsafe { fixed (char* p = buffer) written = PdfiumNative.FPDFText_GetText(text, 0, chars, p); }
            var s = new string(buffer, 0, Math.Max(0, written - 1)); // drop the terminating NUL
            return s.Replace("\r\n", "\n").Replace('\r', '\n');
        }
        finally
        {
            PdfiumNative.FPDFText_ClosePage(text);
            PdfiumNative.FPDF_ClosePage(page);
        }
    });

    private static (IntPtr Page, IntPtr Text) LoadTextPage(IntPtr doc, int pageIndex)
    {
        var page = PdfiumNative.FPDF_LoadPage(doc, pageIndex);
        if (page == IntPtr.Zero)
            throw new InvalidOperationException($"PDFium could not load page {pageIndex}.");
        var text = PdfiumNative.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero)
        {
            PdfiumNative.FPDF_ClosePage(page);
            throw new InvalidOperationException($"PDFium could not read the text of page {pageIndex}.");
        }
        return (page, text);
    }

    /// <summary>One character (UTF-16 unit) of a page, positioned in rendered orientation (points, top-left origin).</summary>
    private readonly record struct Glyph(
        char Ch, bool IsSeparator, bool SpaceBefore, double Left, double Top, double Right, double Bottom, double FontSize, int Orientation)
    {
        public bool IsVertical => Orientation is 90 or 270;

        /// <summary>Extent along the reading direction, increasing in reading order.</summary>
        public (double Start, double End) Along => Orientation switch
        {
            0 => (Left, Right),
            180 => (-Right, -Left),
            90 => (-Bottom, -Top),
            _ => (Top, Bottom),
        };

        /// <summary>Centre across the reading direction.</summary>
        public double Across => IsVertical ? (Left + Right) / 2 : (Top + Bottom) / 2;
    }

    private sealed record PageGlyphs(double Width, double Height, List<Glyph> Glyphs);

    /// <summary>Reads every character of a page under the PDFium gate; segmentation happens afterwards, outside it.</summary>
    private PageGlyphs ReadPage(byte[] pdf, int pageIndex) => _host.WithDocument(pdf, (doc, _) =>
    {
        var (page, text) = LoadTextPage(doc, pageIndex);
        try
        {
            // Rendered size in points (already honours /Rotate). Page space → rendered space is an affine
            // map (crop-box offset + rotation + y flip). FPDF_PageToDevice only yields integers, so it is
            // sampled at 1000 device units per point over a 1000 pt span, which is exact to ~1e-6 pt.
            const int Scale = 1000;
            var width = PdfiumNative.FPDF_GetPageWidthF(page);
            var height = PdfiumNative.FPDF_GetPageHeightF(page);
            if (width <= 0 || height <= 0) return new PageGlyphs(width, height, []);

            int sizeX = (int)Math.Round(width * Scale), sizeY = (int)Math.Round(height * Scale);
            PdfiumNative.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, 0, 0, out var ox, out var oy);
            PdfiumNative.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, 1000, 0, out var xx, out var xy);
            PdfiumNative.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, 0, 1000, out var yx, out var yy);
            // rendered = origin + pageX * ax + pageY * ay   (points)
            double o0 = ox / (double)Scale, o1 = oy / (double)Scale;
            double ax0 = (xx - ox) / (1000.0 * Scale), ax1 = (xy - oy) / (1000.0 * Scale);
            double ay0 = (yx - ox) / (1000.0 * Scale), ay1 = (yy - oy) / (1000.0 * Scale);

            var count = Math.Max(0, PdfiumNative.FPDFText_CountChars(text));
            var glyphs = new List<Glyph>(count);
            var previousWasSeparator = false;
            for (var i = 0; i < count; i++)
            {
                var u = PdfiumNative.FPDFText_GetUnicode(text, i);
                var ch = u <= 0xFFFF ? (char)u : '�';
                var separator = u == 0 || u == 0xFFFE || u == 0xFFFF || char.IsWhiteSpace(ch) || char.IsControl(ch);
                if (separator || PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) == 0)
                {
                    previousWasSeparator = true; // whitespace, or an unplaceable glyph: either way a word break
                    glyphs.Add(new Glyph(ch, true, false, 0, 0, 0, 0, 0, 0));
                    continue;
                }

                // Map the two opposite corners and re-normalise (the map is a 90° multiple plus a translation).
                double x1 = o0 + l * ax0 + b * ay0, y1 = o1 + l * ax1 + b * ay1;
                double x2 = o0 + r * ax0 + t * ay0, y2 = o1 + r * ax1 + t * ay1;

                // Reading direction in rendered space: the glyph direction through the same map. PDFium reports the angle clockwise (a glyph running up the page has 3π/2), hence the minus on sin.
                var angle = PdfiumNative.FPDFText_GetCharAngle(text, i);
                if (angle < 0) angle = 0;
                double ca = Math.Cos(angle), sa = -Math.Sin(angle);
                double dx = ca * ax0 + sa * ay0, dy = ca * ax1 + sa * ay1;
                var orientation = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 180) : (dy < 0 ? 90 : 270);

                var size = PdfiumNative.FPDFText_GetFontSize(text, i);
                glyphs.Add(new Glyph(ch, false, previousWasSeparator, Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2),
                    size > 0 ? size : 12, orientation));
                previousWasSeparator = false;
            }
            return new PageGlyphs(width, height, glyphs);
        }
        finally
        {
            PdfiumNative.FPDFText_ClosePage(text);
            PdfiumNative.FPDF_ClosePage(page);
        }
    });

    // ── segmentation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Words of a page in content order, boxes normalised in rendered orientation. Vertical text (rendered
    /// orientation) becomes one entry per vertical line with <c>IsHorizontal = false</c> unless
    /// <paramref name="splitVerticalIntoWords"/> is set, in which case it is split into words like the rest.
    /// </summary>
    private static List<TextWord> WordsOf(PageGlyphs page, int pageIndex, bool splitVerticalIntoWords)
    {
        var words = new List<TextWord>();
        var source = splitVerticalIntoWords ? page.Glyphs : page.Glyphs.Where(g => g.IsSeparator || !g.IsVertical).ToList();
        foreach (var w in SplitWords(source))
            words.Add(ToWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, w.FontSize, w.Orientation is 0 or 180, page, pageIndex));

        if (!splitVerticalIntoWords)
            foreach (var line in VerticalLines(page.Glyphs))
                words.Add(ToWord(line.Text, line.Left, line.Top, line.Right, line.Bottom, line.FontSize, false, page, pageIndex));
        return words;
    }

    private static TextWord ToWord(string text, double left, double top, double right, double bottom, double fontSize, bool horizontal, PageGlyphs page, int pageIndex) =>
        new()
        {
            Text = text,
            PageIndex = pageIndex,
            BoundingBox = new PdfRect(left / page.Width, top / page.Height, right / page.Width, bottom / page.Height),
            FontSizePoints = fontSize,
            IsHorizontal = horizontal,
        };

    private sealed class WordBuilder
    {
        public readonly StringBuilder Text = new();
        public double Left = double.MaxValue, Top = double.MaxValue, Right = double.MinValue, Bottom = double.MinValue;
        public double FontSize;
        public int Orientation;
        public Glyph Last;
    }

    private readonly record struct RawWord(string Text, double Left, double Top, double Right, double Bottom, double FontSize, int Orientation);

    /// <summary>
    /// Splits glyphs into words at whitespace (real or generated by PDFium), at line changes, at orientation
    /// changes and at gaps / backward jumps along the reading direction that PDFium did not mark with a space.
    /// </summary>
    private static IEnumerable<RawWord> SplitWords(IReadOnlyList<Glyph> glyphs)
    {
        WordBuilder? cur = null;
        foreach (var g in glyphs)
        {
            if (g.IsSeparator)
            {
                if (cur is not null) { yield return Finish(cur); cur = null; }
                continue;
            }
            if (cur is not null && !Continues(cur.Last, g))
            {
                yield return Finish(cur);
                cur = null;
            }
            cur ??= new WordBuilder { FontSize = g.FontSize, Orientation = g.Orientation };
            cur.Text.Append(g.Ch);
            cur.Left = Math.Min(cur.Left, g.Left);
            cur.Top = Math.Min(cur.Top, g.Top);
            cur.Right = Math.Max(cur.Right, g.Right);
            cur.Bottom = Math.Max(cur.Bottom, g.Bottom);
            cur.Last = g;
        }
        if (cur is not null) yield return Finish(cur);

        static RawWord Finish(WordBuilder w) => new(w.Text.ToString(), w.Left, w.Top, w.Right, w.Bottom, w.FontSize, w.Orientation);
    }

    private static bool Continues(Glyph prev, Glyph next)
    {
        if (prev.Orientation != next.Orientation) return false;
        var size = Math.Max(prev.FontSize, next.FontSize);
        if (Math.Abs(next.Across - prev.Across) > size * 0.6) return false; // another line

        var (pStart, pEnd) = prev.Along;
        var (nStart, _) = next.Along;
        if (nStart < pStart - size * 0.1) return false; // pen jumped backwards
        return nStart - pEnd <= size * 0.4;             // gap too wide to sit inside one word
    }

    /// <summary>
    /// Vertical text (e.g. rotated column headers) as one entry per vertical line, rebuilt from the glyphs:
    /// glyphs are grouped by cross-axis position, then read in content order with a space wherever PDFium
    /// saw one or the pen jumps.
    /// </summary>
    private static IEnumerable<(string Text, double Left, double Top, double Right, double Bottom, double FontSize)> VerticalLines(IReadOnlyList<Glyph> glyphs)
    {
        var rotated = glyphs.Select((g, i) => (G: g, Index: i)).Where(x => !x.G.IsSeparator && x.G.IsVertical).ToList();
        var lines = new List<List<(Glyph G, int Index)>>();
        foreach (var x in rotated.OrderBy(x => x.G.Across))
        {
            var line = lines.LastOrDefault();
            var lineX = line is null ? double.NaN : line.Average(l => l.G.Across);
            if (line is null || Math.Abs(x.G.Across - lineX) > Math.Max(1, x.G.FontSize) * 0.45) lines.Add(line = []);
            line.Add(x);
        }
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(l => l.Index).ToList();
            var sb = new StringBuilder();
            for (var i = 0; i < ordered.Count; i++)
            {
                var g = ordered[i].G;
                if (i > 0)
                {
                    var gap = g.Along.Start - ordered[i - 1].G.Along.End;
                    if (g.SpaceBefore || Math.Abs(gap) > g.FontSize * 0.5) sb.Append(' ');
                }
                sb.Append(g.Ch);
            }
            var text = sb.ToString().Trim();
            if (text.Length == 0) continue;
            yield return (text, line.Min(l => l.G.Left), line.Min(l => l.G.Top), line.Max(l => l.G.Right), line.Max(l => l.G.Bottom), line[0].G.FontSize);
        }
    }

    // ── matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Phrase-aware match over a page's words. The words are joined with single spaces into one searchable
    /// string (so a phrase matches across words and line breaks); each match maps back to the boxes of every
    /// word it overlaps. <paramref name="needle"/> must already be whitespace-collapsed and non-empty.
    /// Pure and PDFium-free so the matching logic can be unit-tested directly.
    /// </summary>
    internal static IReadOnlyList<TextSearchResult> FindMatches(
        IReadOnlyList<(string Text, PdfRect Box)> words, int pageIndex, string needle, TextSearchOptions options)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var sb = new StringBuilder();
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
        var sb = new StringBuilder(s.Length);
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
}
