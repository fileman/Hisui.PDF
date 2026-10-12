using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfFormDetectorTests
{
    private readonly PdfFormDetector _sut = new();

    private static byte[] Build(Action<XGraphics> draw, int rotate = 0)
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var gfx = XGraphics.FromPdfPage(page)) draw(gfx);
        if (rotate != 0) page.Rotate = rotate;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void Blanks(XGraphics gfx)
    {
        var font = new XFont("Arial", 10);
        gfx.DrawString("NOME ____________________________", font, XBrushes.Black, new XPoint(50, 100));
        gfx.DrawString("E-MAIL ____________________________", font, XBrushes.Black, new XPoint(50, 130));
    }

    [Fact]
    public async Task Underscores_BecomeTextFieldsNamedAfterTheirLabel()
    {
        var specs = await _sut.DetectFieldsAsync(Build(Blanks));

        Assert.Equal(["NOME", "E_MAIL"], specs.Select(s => s.Name));
        Assert.All(specs, s => Assert.Equal(PdfFormFieldType.Text, s.Type));
        Assert.True(specs[0].Rect.Top < specs[1].Rect.Top); // reading order, top first
        Assert.True(specs[0].Rect.Width > 0.25);
    }

    [Fact]
    public async Task Grid_PaintedWhiteAndBlankCellsBecomeFields_GreyDoNot()
    {
        var pdf = Build(gfx =>
        {
            for (var row = 0; row < 4; row++)
                for (var col = 0; col < 4; col++)
                {
                    var rect = new XRect(100 + col * 21, 100 + row * 11, 20, 10);
                    var n = row * 4 + col;
                    if (n is 5 or 10) continue;                                          // unpainted → recovered from the lattice
                    gfx.DrawRectangle(n is < 5 or 6 ? XBrushes.White : XBrushes.Gray, rect); // white → field, grey → not
                }
        });

        var specs = await _sut.DetectFieldsAsync(pdf);

        Assert.Equal(8, specs.Count);
        Assert.Equal(8, specs.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public async Task SideBySideGrids_ShorterGridDoesNotInheritTallerGridRows()
    {
        var pdf = Build(gfx =>
        {
            // Left grid 6 rows, right grid 3 rows (beyond a gutter); every cell grey except one white per row.
            void Grid(double x, int rowCount)
            {
                for (var row = 0; row < rowCount; row++)
                    for (var col = 0; col < 3; col++)
                        gfx.DrawRectangle(col == 1 ? XBrushes.White : XBrushes.Gray, new XRect(x + col * 21, 100 + row * 11, 20, 10));
            }
            Grid(100, 6);
            Grid(250, 3);
        });

        var specs = await _sut.DetectFieldsAsync(pdf);

        Assert.Equal(9, specs.Count);
    }

    [Fact]
    public async Task RuledMatrix_EmptyCellsAtRowColumnCrossings_BecomeFields()
    {
        var pdf = Build(gfx =>
        {
            var font = new XFont("Arial", 8);
            string[] cols = ["", "COL1", "COL2"];
            string[] rows = ["ROW1", "ROW2", "ROW3"];
            for (var r = 0; r <= rows.Length; r++)
                for (var c = 0; c < 3; c++)
                {
                    var rect = new XRect(100 + c * 70, 100 + r * 20, 70, 20);
                    gfx.DrawRectangle(XPens.Black, rect);
                    var label = r == 0 ? cols[c] : c == 0 ? rows[r - 1] : "";
                    if (label.Length > 0) gfx.DrawString(label, font, XBrushes.Black, new XPoint(rect.X + 3, rect.Y + 13));
                }
        });

        var specs = await _sut.DetectFieldsAsync(pdf);

        Assert.Equal(6, specs.Count);
        Assert.Contains(specs, s => s.Name == "ROW1__COL1");
        Assert.Contains(specs, s => s.Name == "ROW3__COL2");
    }

    [Fact]
    public async Task FewWhiteRectangles_AreNotAGrid()
    {
        var pdf = Build(gfx =>
        {
            gfx.DrawRectangle(XBrushes.White, new XRect(100, 100, 20, 10));
            gfx.DrawRectangle(XBrushes.White, new XRect(130, 100, 20, 10));
        });
        Assert.Empty(await _sut.DetectFieldsAsync(pdf));
    }

    [Fact]
    public async Task AreasCoveredByExistingFields_AreSkipped()
    {
        var pdf = Build(Blanks);
        var first = await _sut.DetectFieldsAsync(pdf);
        var withFields = await new PdfFormService(new PdfiumRenderer()).AddFieldsAsync(pdf, first);

        Assert.Empty(await _sut.DetectFieldsAsync(withFields));
    }

    [Theory]
    [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task RotatedPage_RectsAreInRenderedOrientation(int rotate)
    {
        var plain = await _sut.DetectFieldsAsync(Build(Blanks));
        var rotated = await _sut.DetectFieldsAsync(Build(Blanks, rotate));

        Assert.Equal(plain.Count, rotated.Count);
        foreach (var field in plain) // order differs (reading order follows the rendered page): match by name
        {
            var expected = field.Rect.RotateClockwise(rotate);
            var actual = rotated.Single(r => r.Name == field.Name).Rect;
            Assert.Equal(expected.Left, actual.Left, 2);
            Assert.Equal(expected.Top, actual.Top, 2);
            Assert.Equal(expected.Right, actual.Right, 2);
            Assert.Equal(expected.Bottom, actual.Bottom, 2);
        }
    }

    [Fact]
    public async Task DetectedFields_SurviveSaveThroughSession()
    {
        var forms = new PdfFormService(new PdfiumRenderer());
        var pdf = Build(Blanks);
        var withFields = await forms.AddFieldsAsync(pdf, await _sut.DetectFieldsAsync(pdf));

        var session = new PdfDocumentSession();
        session.AddSource(withFields, 1);
        var saved = await new PdfPageService().BuildFromSessionAsync(session);

        var fields = await forms.ReadFieldsAsync(saved);
        Assert.Equal(["NOME", "E_MAIL"], fields.Select(f => f.Name));
    }
}
