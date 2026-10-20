namespace Hisui.Pdf.Core.Printing;

/// <summary>How document pages are arranged on printed sheets.</summary>
public enum PrintLayoutMode
{
    /// <summary>One page per sheet, scaled per <see cref="PrintScaling"/>.</summary>
    Size,

    /// <summary>Each page enlarged and split across several overlapping sheets.</summary>
    Poster,

    /// <summary>Several pages reduced onto one sheet (n-up).</summary>
    Multiple,

    /// <summary>Pages imposed two per sheet side in fold-and-staple order.</summary>
    Booklet,
}

/// <summary>Page scaling in <see cref="PrintLayoutMode.Size"/> mode.</summary>
public enum PrintScaling
{
    /// <summary>Scale every page up or down to fill the printable area.</summary>
    Fit,

    /// <summary>Shrink pages larger than the printable area; leave the rest at actual size.</summary>
    ShrinkOversized,

    /// <summary>Print at 100%, clipping whatever falls outside the printable area.</summary>
    ActualSize,

    /// <summary>Print at <see cref="PrintLayoutOptions.CustomScale"/> percent.</summary>
    Custom,
}

/// <summary>Sheet orientation. <see cref="Auto"/> picks whatever suits the content best.</summary>
public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape,
}

/// <summary>Fill order of the cells in <see cref="PrintLayoutMode.Multiple"/> mode.</summary>
public enum PrintPageOrder
{
    /// <summary>Left to right, then down.</summary>
    Horizontal,

    /// <summary>Top to bottom, then right.</summary>
    Vertical,
}

/// <summary>Spine side of a booklet.</summary>
public enum BookletBinding
{
    Left,
    Right,
}

/// <summary>Which sheet sides a booklet job prints (both, or one pass of a manual duplex).</summary>
public enum BookletSides
{
    Both,
    FrontOnly,
    BackOnly,
}

/// <summary>A size in PDF points (1/72 inch).</summary>
public readonly record struct PrintSize(double Width, double Height)
{
    public bool IsLandscape => Width > Height;

    public PrintSize Swapped => new(Height, Width);
}

/// <summary>A rectangle in PDF points, origin top-left, y down.</summary>
public readonly record struct PrintRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// A paper the printer offers, in portrait points. <paramref name="Margin"/> is the unprintable border the
/// device needs on every side (the largest of its hard margins, so it holds in either orientation).
/// </summary>
public sealed record PrintPaper(string Name, double Width, double Height, double Margin = 0)
{
    /// <summary>The printable area for the given orientation.</summary>
    public PrintSize Printable(bool landscape)
    {
        var size = new PrintSize(Math.Max(1, Width - 2 * Margin), Math.Max(1, Height - 2 * Margin));
        return landscape ? size.Swapped : size;
    }
}

/// <summary>Every user-facing layout choice of the print dialog. Lengths are in points.</summary>
public sealed record PrintLayoutOptions
{
    public PrintLayoutMode Mode { get; init; } = PrintLayoutMode.Size;
    public PrintOrientation Orientation { get; init; } = PrintOrientation.Auto;

    // Size
    public PrintScaling Scaling { get; init; } = PrintScaling.ShrinkOversized;
    public double CustomScale { get; init; } = 100;

    /// <summary>Size mode only: print each page on the printer paper closest to the page's own size.</summary>
    public bool MatchPaperToPage { get; init; }

    // Poster
    public double PosterScale { get; init; } = 200;
    public double PosterOverlap { get; init; } = 10 * PrintLayoutEngine.PointsPerMillimetre;
    public bool PosterCutMarks { get; init; } = true;
    public bool PosterLabels { get; init; } = true;

    // Multiple
    public int PagesPerSheet { get; init; } = 4;
    public PrintPageOrder PageOrder { get; init; } = PrintPageOrder.Horizontal;
    public bool PageBorders { get; init; } = true;
    public bool AutoRotatePages { get; init; } = true;

    // Booklet
    public BookletBinding Binding { get; init; } = BookletBinding.Left;
    public BookletSides BookletSides { get; init; } = BookletSides.Both;
    public double BookletGutter { get; init; }

    /// <summary>Sheets per folded signature; 0 folds the whole booklet as one signature.</summary>
    public int SignatureSheets { get; init; }
}

/// <summary>
/// One page drawn on a sheet. <see cref="Bounds"/> is the box the page occupies on the sheet after any
/// rotation, in printable-area points; it may extend past the sheet (actual size, poster tiles), in which
/// case the excess is clipped. <see cref="Rotated"/> turns the page 90° clockwise into that box.
/// </summary>
public sealed record PrintPlacement(int PageIndex, PrintRect Bounds, bool Rotated, bool Border);

/// <summary>A thin guide line (poster cut marks), in printable-area points.</summary>
public sealed record PrintMark(double X1, double Y1, double X2, double Y2);

/// <summary>A small caption printed on the sheet (poster tile labels), anchored at its top-left.</summary>
public sealed record PrintLabel(string Text, double X, double Y);

/// <summary>
/// One physical sheet side. Coordinates are relative to the top-left of the printable area of
/// <see cref="Paper"/> in the sheet's orientation, whose extent is <see cref="Size"/>.
/// </summary>
public sealed record PrintSheet(
    PrintPaper Paper,
    bool Landscape,
    PrintSize Size,
    IReadOnlyList<PrintPlacement> Placements,
    IReadOnlyList<PrintMark> Marks,
    IReadOnlyList<PrintLabel> Labels);

/// <summary>
/// Turns a page list plus <see cref="PrintLayoutOptions"/> into the sequence of sheets to print. Pure
/// geometry, shared by the print dialog preview and the print path so both always agree.
/// </summary>
public static class PrintLayoutEngine
{
    public const double PointsPerMillimetre = 72 / 25.4;

    /// <summary>Space between n-up cells.</summary>
    internal const double CellGap = 6;

    // Papers within this many points of a page count as the same size (rounding in page boxes / drivers).
    private const double PaperTolerance = 3;

    public static readonly IReadOnlyList<int> SupportedPagesPerSheet = [2, 4, 6, 9, 16];

    /// <summary>
    /// Lays out <paramref name="pages"/> (zero-based indices, in print order). <paramref name="pageSize"/>
    /// returns a page's displayed size in points (rotation applied). <paramref name="paperIndex"/> selects
    /// the paper from <paramref name="papers"/> unless <see cref="PrintLayoutOptions.MatchPaperToPage"/> applies.
    /// </summary>
    public static IReadOnlyList<PrintSheet> Build(
        IReadOnlyList<int> pages,
        Func<int, PrintSize> pageSize,
        IReadOnlyList<PrintPaper> papers,
        int paperIndex,
        PrintLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(pageSize);
        ArgumentNullException.ThrowIfNull(papers);
        ArgumentNullException.ThrowIfNull(options);
        if (papers.Count == 0) throw new ArgumentException("At least one paper is required.", nameof(papers));
        if (pages.Count == 0) return [];

        var paper = papers[Math.Clamp(paperIndex, 0, papers.Count - 1)];
        return options.Mode switch
        {
            PrintLayoutMode.Poster => Poster(pages, pageSize, paper, options),
            PrintLayoutMode.Multiple => Multiple(pages, pageSize, paper, options),
            PrintLayoutMode.Booklet => Booklet(pages, pageSize, paper, options),
            _ => Size(pages, pageSize, papers, paper, options),
        };
    }

    /// <summary>The smallest paper the page fits on (either way round), else the largest one.</summary>
    public static PrintPaper ClosestPaper(IReadOnlyList<PrintPaper> papers, PrintSize page)
    {
        PrintPaper? best = null;
        PrintPaper largest = papers[0];
        foreach (var paper in papers)
        {
            var area = paper.Width * paper.Height;
            if (area > largest.Width * largest.Height) largest = paper;

            var shortPage = Math.Min(page.Width, page.Height);
            var longPage = Math.Max(page.Width, page.Height);
            var fits = shortPage <= Math.Min(paper.Width, paper.Height) + PaperTolerance
                && longPage <= Math.Max(paper.Width, paper.Height) + PaperTolerance;
            if (fits && (best is null || area < best.Width * best.Height)) best = paper;
        }
        return best ?? largest;
    }

    // ── Size ──────────────────────────────────────────────────────────────────

    private static List<PrintSheet> Size(
        IReadOnlyList<int> pages, Func<int, PrintSize> pageSize, IReadOnlyList<PrintPaper> papers,
        PrintPaper defaultPaper, PrintLayoutOptions o)
    {
        var sheets = new List<PrintSheet>(pages.Count);
        foreach (var index in pages)
        {
            var page = pageSize(index);
            var paper = o.MatchPaperToPage ? ClosestPaper(papers, page) : defaultPaper;
            var landscape = o.Orientation switch
            {
                PrintOrientation.Landscape => true,
                PrintOrientation.Portrait => false,
                _ => page.IsLandscape,
            };
            var box = paper.Printable(landscape);

            var fit = Math.Min(box.Width / page.Width, box.Height / page.Height);
            var scale = o.Scaling switch
            {
                PrintScaling.Fit => fit,
                PrintScaling.ActualSize => 1,
                PrintScaling.Custom => Math.Clamp(o.CustomScale, 1, 1000) / 100,
                _ => Math.Min(1, fit),
            };

            var w = page.Width * scale;
            var h = page.Height * scale;
            var placement = new PrintPlacement(index, new PrintRect((box.Width - w) / 2, (box.Height - h) / 2, w, h), false, false);
            sheets.Add(new PrintSheet(paper, landscape, box, [placement], [], []));
        }
        return sheets;
    }

    // ── Multiple (n-up) ───────────────────────────────────────────────────────

    private static List<PrintSheet> Multiple(
        IReadOnlyList<int> pages, Func<int, PrintSize> pageSize, PrintPaper paper, PrintLayoutOptions o)
    {
        var n = SupportedPagesPerSheet.Contains(o.PagesPerSheet) ? o.PagesPerSheet : 4;
        var first = pageSize(pages[0]);

        var landscape = o.Orientation switch
        {
            PrintOrientation.Landscape => true,
            PrintOrientation.Portrait => false,
            _ => PreferLandscapeCells(paper, n, first, o.AutoRotatePages),
        };

        var box = paper.Printable(landscape);
        var (cols, rows) = Grid(n, landscape);
        var cellW = (box.Width - (cols - 1) * CellGap) / cols;
        var cellH = (box.Height - (rows - 1) * CellGap) / rows;

        var sheets = new List<PrintSheet>((pages.Count + n - 1) / n);
        for (var start = 0; start < pages.Count; start += n)
        {
            var placements = new List<PrintPlacement>(n);
            for (var k = 0; k < n && start + k < pages.Count; k++)
            {
                var (row, col) = o.PageOrder == PrintPageOrder.Vertical ? (k % rows, k / rows) : (k / cols, k % cols);
                var cell = new PrintRect(col * (cellW + CellGap), row * (cellH + CellGap), cellW, cellH);
                var index = pages[start + k];
                placements.Add(FitInCell(index, pageSize(index), cell, o.AutoRotatePages, o.PageBorders));
            }
            sheets.Add(new PrintSheet(paper, landscape, box, placements, [], []));
        }
        return sheets;
    }

    /// <summary>Columns x rows for <paramref name="n"/> cells: the long side of the sheet takes the larger count.</summary>
    internal static (int Cols, int Rows) Grid(int n, bool landscape)
    {
        var (shortSide, longSide) = n switch
        {
            2 => (1, 2),
            6 => (2, 3),
            9 => (3, 3),
            16 => (4, 4),
            _ => (2, 2),
        };
        return landscape ? (longSide, shortSide) : (shortSide, longSide);
    }

    /// <summary>
    /// Auto orientation for n-up: the sheet orientation that prints the pages larger; on a tie, the one that
    /// keeps them upright (2-up portrait pages go side by side on landscape, not turned on portrait).
    /// </summary>
    private static bool PreferLandscapeCells(PrintPaper paper, int n, PrintSize page, bool autoRotate)
    {
        var (portraitScale, portraitUpright) = CellScale(false);
        var (landscapeScale, landscapeUpright) = CellScale(true);
        if (Math.Abs(landscapeScale - portraitScale) > 1e-6) return landscapeScale > portraitScale;
        return landscapeUpright && !portraitUpright;

        (double Scale, bool Upright) CellScale(bool landscape)
        {
            var box = paper.Printable(landscape);
            var (cols, rows) = Grid(n, landscape);
            var cell = new PrintSize((box.Width - (cols - 1) * CellGap) / cols, (box.Height - (rows - 1) * CellGap) / rows);
            var upright = Math.Min(cell.Width / page.Width, cell.Height / page.Height);
            var turned = Math.Min(cell.Width / page.Height, cell.Height / page.Width);
            return autoRotate && turned > upright + 1e-6 ? (turned, false) : (upright, true);
        }
    }

    private static PrintPlacement FitInCell(int index, PrintSize page, PrintRect cell, bool autoRotate, bool border)
    {
        var rotated = autoRotate
            && page.Width != page.Height
            && page.IsLandscape != (cell.Width > cell.Height)
            && Math.Min(cell.Width / page.Height, cell.Height / page.Width) > Math.Min(cell.Width / page.Width, cell.Height / page.Height);
        var effective = rotated ? page.Swapped : page;
        var scale = Math.Min(cell.Width / effective.Width, cell.Height / effective.Height);
        var w = effective.Width * scale;
        var h = effective.Height * scale;
        return new PrintPlacement(index, new PrintRect(cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h), rotated, border);
    }

    // ── Poster ────────────────────────────────────────────────────────────────

    private static List<PrintSheet> Poster(
        IReadOnlyList<int> pages, Func<int, PrintSize> pageSize, PrintPaper paper, PrintLayoutOptions o)
    {
        var sheets = new List<PrintSheet>();
        var scale = Math.Clamp(o.PosterScale, 10, 2000) / 100;

        foreach (var index in pages)
        {
            var page = pageSize(index);
            var poster = new PrintSize(page.Width * scale, page.Height * scale);

            bool landscape;
            if (o.Orientation == PrintOrientation.Auto)
            {
                var portraitTiles = TileCount(poster, paper.Printable(false), o.PosterOverlap);
                var landscapeTiles = TileCount(poster, paper.Printable(true), o.PosterOverlap);
                var p = portraitTiles.Cols * portraitTiles.Rows;
                var l = landscapeTiles.Cols * landscapeTiles.Rows;
                landscape = l < p || (l == p && page.IsLandscape);
            }
            else
            {
                landscape = o.Orientation == PrintOrientation.Landscape;
            }

            var box = paper.Printable(landscape);
            var overlap = EffectiveOverlap(box, o.PosterOverlap);
            var (cols, rows) = TileCount(poster, box, o.PosterOverlap);
            var stepX = box.Width - overlap;
            var stepY = box.Height - overlap;

            // The poster starts at the first tile's corner; spare paper ends up on the last row / column,
            // so every tile but the outer ones is printed edge to edge.
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var bounds = new PrintRect(-col * stepX, -row * stepY, poster.Width, poster.Height);

                    var marks = new List<PrintMark>();
                    if (o.PosterCutMarks && overlap > 0)
                    {
                        // Trim line where this tile starts overlapping its left / top neighbour.
                        if (col > 0) marks.Add(new PrintMark(overlap, 0, overlap, box.Height));
                        if (row > 0) marks.Add(new PrintMark(0, overlap, box.Width, overlap));
                    }

                    var labels = new List<PrintLabel>();
                    if (o.PosterLabels && (cols > 1 || rows > 1))
                        labels.Add(new PrintLabel($"{TileName(row, col)} · {index + 1}", (col > 0 ? overlap : 0) + 4, (row > 0 ? overlap : 0) + 4));

                    sheets.Add(new PrintSheet(paper, landscape, box, [new PrintPlacement(index, bounds, false, false)], marks, labels));
                }
            }
        }
        return sheets;
    }

    /// <summary>Overlap clamped so a tile always advances by at least half its size.</summary>
    private static double EffectiveOverlap(PrintSize box, double overlap) =>
        Math.Clamp(overlap, 0, Math.Min(box.Width, box.Height) / 2);

    internal static (int Cols, int Rows) TileCount(PrintSize poster, PrintSize box, double overlap)
    {
        overlap = EffectiveOverlap(box, overlap);
        return (Tiles(poster.Width, box.Width), Tiles(poster.Height, box.Height));

        int Tiles(double length, double tile) =>
            length <= tile + 1e-6 ? 1 : (int)Math.Ceiling((length - overlap) / (tile - overlap) - 1e-9);
    }

    /// <summary>Spreadsheet-style tile name: row letter(s) and 1-based column ("A1", "B3", "AA2").</summary>
    internal static string TileName(int row, int col)
    {
        var letters = string.Empty;
        for (var r = row + 1; r > 0; r = (r - 1) / 26)
            letters = (char)('A' + (r - 1) % 26) + letters;
        return letters + (col + 1);
    }

    // ── Booklet ───────────────────────────────────────────────────────────────

    private static List<PrintSheet> Booklet(
        IReadOnlyList<int> pages, Func<int, PrintSize> pageSize, PrintPaper paper, PrintLayoutOptions o)
    {
        var first = pageSize(pages[0]);
        // The fold runs across the long side of the sheet: landscape sheets carry the pages side by side,
        // portrait sheets one above the other (calendar style, for landscape pages).
        var landscape = o.Orientation switch
        {
            PrintOrientation.Landscape => true,
            PrintOrientation.Portrait => false,
            _ => HalfScale(paper, true, first, o.BookletGutter) >= HalfScale(paper, false, first, o.BookletGutter),
        };
        var box = paper.Printable(landscape);
        var gutter = Math.Clamp(o.BookletGutter, 0, Math.Max(box.Width, box.Height) / 2);

        // Pad to whole sheets (4 pages each); -1 marks a blank page.
        var padded = pages.ToList();
        while (padded.Count % 4 != 0) padded.Add(-1);

        var signaturePages = o.SignatureSheets > 0 ? o.SignatureSheets * 4 : padded.Count;
        var sheets = new List<PrintSheet>();

        for (var start = 0; start < padded.Count; start += signaturePages)
        {
            var count = Math.Min(signaturePages, padded.Count - start);
            for (var s = 0; s < count / 4; s++)
            {
                var front = (padded[start + count - 1 - 2 * s], padded[start + 2 * s]);
                var back = (padded[start + 2 * s + 1], padded[start + count - 2 - 2 * s]);

                if (o.BookletSides != BookletSides.BackOnly) sheets.Add(Spread(front));
                if (o.BookletSides != BookletSides.FrontOnly) sheets.Add(Spread(back));
            }
        }
        return sheets;

        PrintSheet Spread((int Left, int Right) pair)
        {
            var (near, far) = o.Binding == BookletBinding.Right ? (pair.Right, pair.Left) : (pair.Left, pair.Right);
            var placements = new List<PrintPlacement>(2);

            if (landscape)
            {
                var half = new PrintSize((box.Width - gutter) / 2, box.Height);
                if (near >= 0) placements.Add(AgainstSpine(near, new PrintRect(0, 0, half.Width, half.Height), spineAt: 1));
                if (far >= 0) placements.Add(AgainstSpine(far, new PrintRect(half.Width + gutter, 0, half.Width, half.Height), spineAt: -1));
            }
            else
            {
                var half = new PrintSize(box.Width, (box.Height - gutter) / 2);
                if (near >= 0) placements.Add(AgainstSpine(near, new PrintRect(0, 0, half.Width, half.Height), spineAt: 1));
                if (far >= 0) placements.Add(AgainstSpine(far, new PrintRect(0, half.Height + gutter, half.Width, half.Height), spineAt: -1));
            }
            return new PrintSheet(paper, landscape, box, placements, [], []);
        }

        // Fit the page in its half and push it against the fold (spineAt: +1 = far edge, -1 = near edge).
        PrintPlacement AgainstSpine(int index, PrintRect half, int spineAt)
        {
            var page = pageSize(index);
            var scale = Math.Min(half.Width / page.Width, half.Height / page.Height);
            var w = page.Width * scale;
            var h = page.Height * scale;
            double x, y;
            if (landscape)
            {
                x = spineAt > 0 ? half.Right - w : half.X;
                y = half.Y + (half.Height - h) / 2;
            }
            else
            {
                x = half.X + (half.Width - w) / 2;
                y = spineAt > 0 ? half.Bottom - h : half.Y;
            }
            return new PrintPlacement(index, new PrintRect(x, y, w, h), false, false);
        }
    }

    private static double HalfScale(PrintPaper paper, bool landscape, PrintSize page, double gutter)
    {
        var box = paper.Printable(landscape);
        var half = landscape
            ? new PrintSize((box.Width - gutter) / 2, box.Height)
            : new PrintSize(box.Width, (box.Height - gutter) / 2);
        return Math.Min(half.Width / page.Width, half.Height / page.Height);
    }
}
