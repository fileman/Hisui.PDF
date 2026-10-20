using Hisui.Pdf.Core.Printing;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PrintLayoutEngineTests
{
    private static readonly PrintSize A4 = new(595.276, 841.89);
    private static readonly PrintSize A4Landscape = A4.Swapped;
    private static readonly PrintSize A3 = new(841.89, 1190.55);

    private static readonly PrintPaper PaperA4 = new("A4", A4.Width, A4.Height);
    private static readonly PrintPaper PaperA3 = new("A3", A3.Width, A3.Height);
    private static readonly PrintPaper PaperA5 = new("A5", 419.53, 595.276);

    private static IReadOnlyList<PrintSheet> Build(int pageCount, PrintLayoutOptions options, Func<int, PrintSize>? size = null, params PrintPaper[] papers) =>
        PrintLayoutEngine.Build(
            Enumerable.Range(0, pageCount).ToArray(),
            size ?? (_ => A4),
            papers.Length > 0 ? papers : [PaperA4],
            0,
            options);

    // ── Size ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Size_ActualSize_OnSamePaper_FillsSheetAt100Percent()
    {
        var sheet = Assert.Single(Build(1, new() { Scaling = PrintScaling.ActualSize }));
        var p = Assert.Single(sheet.Placements);

        Assert.False(sheet.Landscape);
        Assert.Equal(new PrintRect(0, 0, A4.Width, A4.Height), p.Bounds);
    }

    [Fact]
    public void Size_ShrinkOversized_ShrinksA3ButLeavesA5Alone()
    {
        var sheets = Build(2, new() { Scaling = PrintScaling.ShrinkOversized }, i => i == 0 ? A3 : new PrintSize(419.53, 595.276));

        Assert.Equal(A4.Width, sheets[0].Placements[0].Bounds.Width, 3); // A3 scaled down to the A4 width
        Assert.Equal(419.53, sheets[1].Placements[0].Bounds.Width, 3);   // A5 stays at 100%
    }

    [Fact]
    public void Size_Fit_EnlargesSmallPages()
    {
        var p = Build(1, new() { Scaling = PrintScaling.Fit }, _ => new PrintSize(419.53, 595.276))[0].Placements[0];
        Assert.Equal(A4.Height, p.Bounds.Height, 1);
    }

    [Fact]
    public void Size_Custom_ScalesAndCentres()
    {
        var p = Build(1, new() { Scaling = PrintScaling.Custom, CustomScale = 50 })[0].Placements[0];

        Assert.Equal(A4.Width / 2, p.Bounds.Width, 3);
        Assert.Equal(A4.Width / 4, p.Bounds.X, 3);
    }

    [Fact]
    public void Size_AutoOrientation_FollowsEachPage()
    {
        var sheets = Build(2, new(), i => i == 0 ? A4 : A4Landscape);

        Assert.False(sheets[0].Landscape);
        Assert.True(sheets[1].Landscape);
    }

    [Fact]
    public void Size_ForcedPortrait_DoesNotRotateLandscapePages()
    {
        var sheet = Build(1, new() { Orientation = PrintOrientation.Portrait, Scaling = PrintScaling.Fit }, _ => A4Landscape)[0];

        Assert.False(sheet.Landscape);
        Assert.False(sheet.Placements[0].Rotated);
        Assert.Equal(A4.Width, sheet.Placements[0].Bounds.Width, 3);
    }

    [Fact]
    public void Size_MatchPaperToPage_PicksSmallestPaperThatFits()
    {
        var sheets = Build(3, new() { MatchPaperToPage = true },
            i => i switch { 0 => A3, 1 => A4Landscape, _ => new PrintSize(2000, 3000) },
            PaperA5, PaperA4, PaperA3);

        Assert.Equal("A3", sheets[0].Paper.Name);
        Assert.Equal("A4", sheets[1].Paper.Name); // orientation doesn't matter for the match
        Assert.Equal("A3", sheets[2].Paper.Name); // nothing fits: the largest paper
    }

    [Fact]
    public void Size_RespectsHardMargins()
    {
        var paper = new PrintPaper("A4", A4.Width, A4.Height, Margin: 12);
        var sheet = PrintLayoutEngine.Build([0], _ => A4, [paper], 0, new() { Scaling = PrintScaling.ShrinkOversized })[0];

        Assert.Equal(A4.Width - 24, sheet.Size.Width, 3);
        Assert.True(sheet.Placements[0].Bounds.Width <= sheet.Size.Width + 1e-6);
    }

    // ── Multiple ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 10, 5)]
    [InlineData(4, 10, 3)]
    [InlineData(6, 13, 3)]
    [InlineData(16, 16, 1)]
    public void Multiple_GroupsPagesPerSheet(int perSheet, int pages, int expectedSheets)
    {
        var sheets = Build(pages, new() { Mode = PrintLayoutMode.Multiple, PagesPerSheet = perSheet });

        Assert.Equal(expectedSheets, sheets.Count);
        Assert.Equal(Enumerable.Range(0, pages), sheets.SelectMany(s => s.Placements).Select(p => p.PageIndex));
    }

    [Fact]
    public void Multiple_TwoUpPortraitPages_GoLandscapeSideBySide()
    {
        var sheet = Build(2, new() { Mode = PrintLayoutMode.Multiple, PagesPerSheet = 2 })[0];

        Assert.True(sheet.Landscape);
        Assert.True(sheet.Placements[1].Bounds.X > sheet.Placements[0].Bounds.Right);
        Assert.All(sheet.Placements, p => Assert.False(p.Rotated));
    }

    [Fact]
    public void Multiple_VerticalOrder_FillsColumnsFirst()
    {
        var sheet = Build(4, new() { Mode = PrintLayoutMode.Multiple, PagesPerSheet = 4, PageOrder = PrintPageOrder.Vertical })[0];

        var p0 = sheet.Placements[0].Bounds;
        var p1 = sheet.Placements[1].Bounds;
        Assert.Equal(p0.X, p1.X, 3);   // page 2 is under page 1
        Assert.True(p1.Y > p0.Bottom);
    }

    [Fact]
    public void Multiple_AutoRotate_TurnsLandscapePagesInPortraitCells()
    {
        var options = new PrintLayoutOptions { Mode = PrintLayoutMode.Multiple, PagesPerSheet = 4, Orientation = PrintOrientation.Portrait };

        var rotated = Build(1, options, _ => A4Landscape)[0].Placements[0];
        var upright = Build(1, options with { AutoRotatePages = false }, _ => A4Landscape)[0].Placements[0];

        Assert.True(rotated.Rotated);
        Assert.False(upright.Rotated);
        Assert.True(rotated.Bounds.Width * rotated.Bounds.Height > upright.Bounds.Width * upright.Bounds.Height);
    }

    [Fact]
    public void Multiple_CellsStayInsideTheSheet()
    {
        foreach (var n in PrintLayoutEngine.SupportedPagesPerSheet)
        {
            var sheet = Build(n, new() { Mode = PrintLayoutMode.Multiple, PagesPerSheet = n })[0];
            Assert.All(sheet.Placements, p =>
            {
                Assert.True(p.Bounds.X >= -1e-6 && p.Bounds.Y >= -1e-6);
                Assert.True(p.Bounds.Right <= sheet.Size.Width + 1e-6 && p.Bounds.Bottom <= sheet.Size.Height + 1e-6);
            });
        }
    }

    // ── Poster ────────────────────────────────────────────────────────────────

    [Fact]
    public void Poster_200Percent_WithoutOverlap_IsTwoByTwo()
    {
        var sheets = Build(1, new() { Mode = PrintLayoutMode.Poster, PosterScale = 200, PosterOverlap = 0 });

        Assert.Equal(4, sheets.Count);
        Assert.All(sheets, s => Assert.Equal(A4.Width * 2, s.Placements[0].Bounds.Width, 3));
        Assert.Equal(["A1 · 1", "A2 · 1", "B1 · 1", "B2 · 1"], sheets.Select(s => s.Labels[0].Text));
    }

    [Fact]
    public void Poster_Overlap_AddsTilesAndShortensTheStep()
    {
        var sheets = Build(1, new() { Mode = PrintLayoutMode.Poster, PosterScale = 200, PosterOverlap = 20, Orientation = PrintOrientation.Portrait });

        // Two A4 widths no longer fit in two overlapping tiles: 3 × 3.
        Assert.Equal(9, sheets.Count);
        Assert.Equal(A4.Width - 20, sheets[0].Placements[0].Bounds.X - sheets[1].Placements[0].Bounds.X, 3);
    }

    [Fact]
    public void Poster_TilesCoverTheWholePoster()
    {
        var sheets = Build(1, new() { Mode = PrintLayoutMode.Poster, PosterScale = 300, PosterOverlap = 15 });
        var poster = sheets[0].Placements[0].Bounds;

        // Every point of the poster lies on some tile: check the far corners.
        bool Covered(double px, double py) => sheets.Any(s =>
        {
            var b = s.Placements[0].Bounds;
            var x = b.X + (px - poster.X); // where the poster point lands on this tile
            var y = b.Y + (py - poster.Y);
            return x >= -1e-6 && y >= -1e-6 && x <= s.Size.Width + 1e-6 && y <= s.Size.Height + 1e-6;
        });
        Assert.True(Covered(poster.X, poster.Y));
        Assert.True(Covered(poster.Right, poster.Bottom));
        Assert.True(Covered(poster.X, poster.Bottom));
        Assert.True(Covered(poster.Right, poster.Y));
    }

    [Fact]
    public void Poster_CutMarksOnlyOnInnerEdges()
    {
        var sheets = Build(1, new() { Mode = PrintLayoutMode.Poster, PosterScale = 200, Orientation = PrintOrientation.Portrait });
        PrintSheet Tile(string name) => sheets.Single(s => s.Labels[0].Text.StartsWith(name + " "));

        Assert.Empty(Tile("A1").Marks);          // top-left tile
        Assert.Single(Tile("A2").Marks);         // left neighbour only
        Assert.Single(Tile("B1").Marks);         // top neighbour only
        Assert.Equal(2, Tile("B2").Marks.Count); // left and top
    }

    [Fact]
    public void Poster_100Percent_IsOneTileWithoutLabels()
    {
        var sheet = Assert.Single(Build(1, new() { Mode = PrintLayoutMode.Poster, PosterScale = 100 }));
        Assert.Empty(sheet.Labels);
    }

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(1, 2, "B3")]
    [InlineData(25, 0, "Z1")]
    [InlineData(26, 1, "AA2")]
    public void TileName_IsSpreadsheetStyle(int row, int col, string expected) =>
        Assert.Equal(expected, PrintLayoutEngine.TileName(row, col));

    // ── Booklet ───────────────────────────────────────────────────────────────

    private static int[] Order(IEnumerable<PrintSheet> sheets) =>
        sheets.SelectMany(s => s.Placements.OrderBy(p => p.Bounds.X).ThenBy(p => p.Bounds.Y).Select(p => p.PageIndex + 1)).ToArray();

    [Fact]
    public void Booklet_EightPages_ImposesInFoldOrder()
    {
        var sheets = Build(8, new() { Mode = PrintLayoutMode.Booklet });

        Assert.Equal(4, sheets.Count);
        Assert.All(sheets, s => Assert.True(s.Landscape));
        Assert.Equal([8, 1, 2, 7, 6, 3, 4, 5], Order(sheets));
    }

    [Fact]
    public void Booklet_PadsWithBlankPages()
    {
        var sheets = Build(5, new() { Mode = PrintLayoutMode.Booklet });

        Assert.Equal(4, sheets.Count);                                  // 5 pages → 8 slots → 2 sheets × 2 sides
        Assert.Equal(5, sheets.Sum(s => s.Placements.Count));           // three slots stay blank
        Assert.Equal([1, 2, 3, 4, 5], Order(sheets).Order());
    }

    [Fact]
    public void Booklet_RightBinding_MirrorsSpreads()
    {
        var sheets = Build(4, new() { Mode = PrintLayoutMode.Booklet, Binding = BookletBinding.Right });
        Assert.Equal([1, 4, 3, 2], Order(sheets));
    }

    [Fact]
    public void Booklet_FrontAndBackOnly_SplitTheSides()
    {
        var front = Build(8, new() { Mode = PrintLayoutMode.Booklet, BookletSides = BookletSides.FrontOnly });
        var back = Build(8, new() { Mode = PrintLayoutMode.Booklet, BookletSides = BookletSides.BackOnly });

        Assert.Equal([8, 1, 6, 3], Order(front));
        Assert.Equal([2, 7, 4, 5], Order(back));
    }

    [Fact]
    public void Booklet_Signatures_FoldInGroups()
    {
        var sheets = Build(8, new() { Mode = PrintLayoutMode.Booklet, SignatureSheets = 1 });
        Assert.Equal([4, 1, 2, 3, 8, 5, 6, 7], Order(sheets));
    }

    [Fact]
    public void Booklet_PagesSitAgainstTheSpine_WithGutter()
    {
        var gutter = 20.0;
        var sheet = Build(4, new() { Mode = PrintLayoutMode.Booklet, BookletGutter = gutter })[0];
        var left = sheet.Placements.MinBy(p => p.Bounds.X)!.Bounds;
        var right = sheet.Placements.MaxBy(p => p.Bounds.X)!.Bounds;

        Assert.Equal(gutter, right.X - left.Right, 3);
        Assert.Equal(sheet.Size.Width / 2, (left.Right + right.X) / 2, 3);
    }

    [Fact]
    public void Booklet_LandscapePages_StackOnAPortraitSheet()
    {
        var sheet = Build(4, new() { Mode = PrintLayoutMode.Booklet }, _ => A4Landscape)[0];

        Assert.False(sheet.Landscape);
        Assert.True(sheet.Placements[1].Bounds.Y >= sheet.Placements[0].Bounds.Bottom - 1e-6);
    }

    [Fact]
    public void EmptyPageList_ProducesNoSheets() =>
        Assert.Empty(Build(0, new()));
}
