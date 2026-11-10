using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Finds the non-text parts of a scanned page (logos, signatures, stamps, photos) so a conversion can keep
/// them as pictures next to the OCR text. Works on a rendered page: confidently recognized words are blanked
/// out, the remaining ink is gathered on a coarse grid, and sizeable connected blobs become figures.
/// </summary>
internal static class ScanFigureDetector
{
    private const int CellPx = 6;              // grid resolution at the render DPI (≈ 1 mm at 150 DPI)
    private const int InkLuminance = 200;      // darker than this is ink, lighter is paper / scanner noise
    private const int MinInkPixelsPerCell = 3; // a cell with fewer dark pixels is a speck
    private const int LinkCells = 2;           // blobs closer than this many cells belong to the same figure
    private const double BorderFraction = 0.015; // scanner edge shadows live in the outer band of the page
    private const double MinAreaFraction = 0.0015;
    private const int MinSidePx = 30;

    /// <summary>Default text test: words at or above this confidence are blanked before looking for ink.</summary>
    public const float TextConfidence = 40;

    /// <summary>Words below this confidence that fall inside a figure belong to it (seal lettering, handwriting).</summary>
    public const float TrustedConfidence = 75;

    // A figure must be taller than this many typical text lines; a single misread or underlined line is not one.
    private const double MinFigureLines = 2.2;

    public sealed record Figure(PdfRect Box, byte[] Png);

    /// <summary>
    /// Returns the figures of <paramref name="page"/> (cropped from the render, PNG) and the indices of the
    /// <paramref name="words"/> they absorb: untrusted words inside a figure are its own marks misread as text.
    /// <paramref name="isText"/> picks the words that are text and are blanked first (default: confidence at least
    /// <see cref="TextConfidence"/>). <paramref name="knownText"/> are boxes of text already taken from the text
    /// layer (e.g. a rotated stamp).
    /// </summary>
    public static (List<Figure> Figures, HashSet<int> AbsorbedWords) Detect(
        RenderedPage page, IReadOnlyList<OcrWord> words, Func<OcrWord, bool>? isText = null, IEnumerable<PdfRect>? knownText = null)
    {
        int w = page.Width, h = page.Height;
        if (w == 0 || h == 0) return ([], []);
        int gw = (w + CellPx - 1) / CellPx, gh = (h + CellPx - 1) / CellPx;

        var blank = new bool[gw * gh];
        foreach (var r in knownText ?? [])
            MarkCells(blank, gw, gh, w, h, new PdfRect(r.Left - 0.004, r.Top - 0.004, r.Right + 0.004, r.Bottom + 0.004));
        foreach (var word in words)
        {
            if (!(isText?.Invoke(word) ?? word.Confidence >= TextConfidence)) continue;
            var b = word.BoundingBox;
            var pad = (b.Bottom - b.Top) * 0.3;
            MarkCells(blank, gw, gh, w, h, new PdfRect(b.Left - pad * h / Math.Max(w, 1), b.Top - pad, b.Right + pad * h / Math.Max(w, 1), b.Bottom + pad));
        }

        var ink = InkGrid(page, gw, gh, blank);
        var boxes = Components(ink, gw, gh);
        boxes = MergeOverlapping(boxes);

        var heights = words.Where(x => x.Confidence >= TrustedConfidence && x.Text.Length >= 3)
            .Select(x => x.BoundingBox.Height * h).Order().ToList();
        var lineHeightPx = heights.Count > 0 ? heights[heights.Count / 2] : h * 0.012;
        var minHeightPx = Math.Max(MinSidePx, MinFigureLines * lineHeightPx);

        var figures = new List<Figure>();
        var absorbed = new HashSet<int>();
        foreach (var (x0, y0, x1, y1) in boxes)
        {
            // Back to pixels, with one cell of breathing room.
            int px0 = Math.Max(0, (x0 - 1) * CellPx), py0 = Math.Max(0, (y0 - 1) * CellPx);
            int px1 = Math.Min(w, (x1 + 2) * CellPx), py1 = Math.Min(h, (y1 + 2) * CellPx);
            int cw = px1 - px0, ch = py1 - py0;
            if (cw < MinSidePx || ch < minHeightPx || (double)cw * ch < MinAreaFraction * w * h) continue;

            var box = new PdfRect((double)px0 / w, (double)py0 / h, (double)px1 / w, (double)py1 / h);
            figures.Add(new Figure(box, PdfiumRenderer.EncodePng(Crop(page, px0, py0, cw, ch))));

            for (var i = 0; i < words.Count; i++)
            {
                if (words[i].Confidence >= TrustedConfidence) continue;
                var c = words[i].BoundingBox;
                double cx = (c.Left + c.Right) / 2, cy = (c.Top + c.Bottom) / 2;
                if (cx >= box.Left && cx <= box.Right && cy >= box.Top && cy <= box.Bottom) absorbed.Add(i);
            }
        }
        return (figures, absorbed);
    }

    private static void MarkCells(bool[] grid, int gw, int gh, int w, int h, PdfRect r)
    {
        int x0 = Math.Clamp((int)(r.Left * w) / CellPx, 0, gw - 1), x1 = Math.Clamp((int)(r.Right * w) / CellPx, 0, gw - 1);
        int y0 = Math.Clamp((int)(r.Top * h) / CellPx, 0, gh - 1), y1 = Math.Clamp((int)(r.Bottom * h) / CellPx, 0, gh - 1);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            grid[y * gw + x] = true;
    }

    private static bool[] InkGrid(RenderedPage page, int gw, int gh, bool[] blank)
    {
        var counts = new int[gw * gh];
        int bx = (int)(page.Width * BorderFraction), by = (int)(page.Height * BorderFraction);
        var px = page.Bgra;
        for (var y = by; y < page.Height - by; y++)
        {
            var row = y * page.Stride;
            var cellRow = y / CellPx * gw;
            for (var x = bx; x < page.Width - bx; x++)
            {
                var i = row + x * 4;
                // Rec. 601 luma from BGRA.
                var lum = (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
                if (lum < InkLuminance) counts[cellRow + x / CellPx]++;
            }
        }

        var ink = new bool[gw * gh];
        for (var i = 0; i < ink.Length; i++) ink[i] = !blank[i] && counts[i] >= MinInkPixelsPerCell;
        return ink;
    }

    /// <summary>Bounding boxes (in cells, inclusive) of ink blobs, linking cells up to <see cref="LinkCells"/> apart.</summary>
    private static List<(int X0, int Y0, int X1, int Y1)> Components(bool[] ink, int gw, int gh)
    {
        var seen = new bool[ink.Length];
        var result = new List<(int, int, int, int)>();
        var stack = new Stack<int>();
        for (var start = 0; start < ink.Length; start++)
        {
            if (!ink[start] || seen[start]) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, cells = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                int cx = c % gw, cy = c / gw;
                cells++;
                x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                for (var dy = -LinkCells; dy <= LinkCells; dy++)
                for (var dx = -LinkCells; dx <= LinkCells; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    var n = ny * gw + nx;
                    if (!ink[n] || seen[n]) continue;
                    seen[n] = true;
                    stack.Push(n);
                }
            }
            if (cells >= 4) result.Add((x0, y0, x1, y1));
        }
        return result;
    }

    private static List<(int X0, int Y0, int X1, int Y1)> MergeOverlapping(List<(int X0, int Y0, int X1, int Y1)> boxes)
    {
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < boxes.Count && !merged; i++)
            for (var j = i + 1; j < boxes.Count && !merged; j++)
            {
                var (a, b) = (boxes[i], boxes[j]);
                if (a.X0 - LinkCells > b.X1 || b.X0 - LinkCells > a.X1 || a.Y0 - LinkCells > b.Y1 || b.Y0 - LinkCells > a.Y1) continue;
                boxes[i] = (Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));
                boxes.RemoveAt(j);
                merged = true;
            }
        }
        return boxes;
    }

    /// <summary>
    /// Typical stem thickness of the ink inside <paramref name="box"/>, in pixels: the mean length of dark
    /// horizontal runs, ignoring runs far longer than the median (underlines, horizontal bars). Bold text has
    /// clearly thicker stems than regular text of the same page.
    /// </summary>
    public static double StrokeWidth(RenderedPage page, PdfRect box)
    {
        int x0 = Math.Clamp((int)(box.Left * page.Width), 0, page.Width), x1 = Math.Clamp((int)Math.Ceiling(box.Right * page.Width), 0, page.Width);
        int y0 = Math.Clamp((int)(box.Top * page.Height), 0, page.Height), y1 = Math.Clamp((int)Math.Ceiling(box.Bottom * page.Height), 0, page.Height);
        var runs = new List<int>();
        for (var y = y0; y < y1; y++)
        {
            var run = 0;
            var row = y * page.Stride;
            for (var x = x0; x < x1; x++)
            {
                var i = row + x * 4;
                var dark = ((page.Bgra[i] * 29 + page.Bgra[i + 1] * 150 + page.Bgra[i + 2] * 77) >> 8) < 128;
                if (dark) run++;
                else if (run > 0) { runs.Add(run); run = 0; }
            }
            if (run > 0) runs.Add(run);
        }
        if (runs.Count < 4) return 0;
        runs.Sort();
        var median = runs[runs.Count / 2];
        return runs.Where(r => r <= 2.5 * median).Average();
    }

    /// <summary>
    /// Height of <paramref name="box"/> (normalised) without an underline: dark, nearly solid rows at the bottom
    /// and the gap above them are trimmed. Underlined words otherwise come out a size or two too large.
    /// </summary>
    public static double InkHeight(RenderedPage page, PdfRect box)
    {
        int x0 = Math.Clamp((int)(box.Left * page.Width), 0, page.Width), x1 = Math.Clamp((int)Math.Ceiling(box.Right * page.Width), 0, page.Width);
        int y0 = Math.Clamp((int)(box.Top * page.Height), 0, page.Height), y1 = Math.Clamp((int)Math.Ceiling(box.Bottom * page.Height), 0, page.Height);
        if (x1 - x0 < 4 || y1 - y0 < 4) return box.Height;

        double DarkFraction(int y)
        {
            var dark = 0;
            for (var x = x0; x < x1; x++)
            {
                var i = y * page.Stride + x * 4;
                if (((page.Bgra[i] * 29 + page.Bgra[i + 1] * 150 + page.Bgra[i + 2] * 77) >> 8) < 160) dark++;
            }
            return dark / (double)(x1 - x0);
        }

        var bottom = y1 - 1;
        var minBottom = y0 + (y1 - y0) / 2;
        while (bottom > minBottom && DarkFraction(bottom) < 0.03) bottom--; // blank rows under the line

        // An underline: a few nearly solid rows, then a clear gap before the glyphs (only descenders cross it).
        var lineRows = 0;
        while (bottom > minBottom && lineRows < 6 && DarkFraction(bottom) > 0.45) { bottom--; lineRows++; }
        if (lineRows == 0 || DarkFraction(bottom) >= 0.15) return box.Height;
        while (bottom > minBottom && DarkFraction(bottom) < 0.15) bottom--;
        return (bottom + 1 - y0) / (double)page.Height;
    }

    internal static RenderedPage Crop(RenderedPage page, int x, int y, int width, int height)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(page.Bgra, (y + row) * page.Stride + x * 4, bytes, row * stride, stride);
        return new RenderedPage(width, height, stride, bytes);
    }
}
