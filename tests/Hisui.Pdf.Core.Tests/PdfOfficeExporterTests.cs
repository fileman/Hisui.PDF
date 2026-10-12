using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Hisui.Pdf.Core.Tests;

public class PdfOfficeExporterTests
{
    private readonly PdfOfficeExporter _sut = new(new PdfTextExtractor(), new PdfiumRenderer());

    private static byte[] TablePdf()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 10);
            string[][] rows = [["Specie", "Quantità", "Prezzo"], ["Acero", "12", "8,50"], ["Olmo campestre", "3", "22,00"]];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < 3; c++)
                    gfx.DrawString(rows[r][c], font, XBrushes.Black, new XPoint(60 + c * 150, 100 + r * 20));
        }
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Xlsx_RebuildsRowsAndColumns_WithNumbers()
    {
        var xlsx = await _sut.ExportXlsxAsync(TablePdf());

        using var doc = SpreadsheetDocument.Open(new MemoryStream(xlsx), false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        var sheet = doc.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var rows = sheet.Descendants<S.Row>().ToList();
        Assert.Equal(3, rows.Count);
        var cells = rows[2].Elements<S.Cell>().ToList();
        Assert.Equal(3, cells.Count);
        Assert.Equal("Olmo campestre", cells[0].InnerText);
        Assert.Equal(S.CellValues.Number, cells[2].DataType!.Value);
        Assert.Equal("22", cells[2].CellValue!.Text);
    }

    [Fact]
    public async Task Pptx_OneValidSlidePerPage()
    {
        var pptx = await _sut.ExportPptxAsync(PdfFixtureBuilder.CreateWithText("Diapositiva", 3), dpi: 50);

        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        Assert.Equal(3, doc.PresentationPart!.SlideParts.Count());
    }

    [Fact]
    public async Task Xlsx_RotatedColumnHeadersBecomeTheHeaderRow()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 9);
            foreach (var (x, text) in new[] { (200.0, "Vaso 1 litro"), (240.0, "Radice nuda") })
            {
                var state = gfx.Save();
                gfx.RotateAtTransform(-90, new XPoint(x, 200));
                gfx.DrawString(text, font, XBrushes.Black, new XPoint(x, 200));
                gfx.Restore(state);
            }
            gfx.DrawString("Acero", font, XBrushes.Black, new XPoint(60, 230));
            gfx.DrawString("3", font, XBrushes.Black, new XPoint(196, 230));
            gfx.DrawString("5", font, XBrushes.Black, new XPoint(236, 230));
        }
        using var ms = new MemoryStream();
        doc.Save(ms);

        var pages = await new PdfTextExtractor().ExtractAllWordsAsync(ms.ToArray());
        var table = PdfOfficeExporter.Table(pages[0]);

        Assert.Contains("Vaso 1 litro", table[0]);
        Assert.Contains("Radice nuda", table[0]);
        Assert.Equal(Array.IndexOf(table[0], "Vaso 1 litro"), Array.IndexOf(table[1], "3"));
    }
}
