using DocumentFormat.OpenXml.Packaging;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfWordExporterTests
{
    [Fact]
    public async Task Export_ProducesDocxWithThePageText()
    {
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 12);
            gfx.DrawString("Hello converted world", font, XBrushes.Black, new XPoint(50, 100));
            gfx.DrawString("Second paragraph here", font, XBrushes.Black, new XPoint(50, 200));
        }
        using var pdf = new MemoryStream();
        doc.Save(pdf);

        var sut = new PdfWordExporter(new PdfiumRenderer(), new PdfTextExtractor(), new PdfImageExtractor());
        var docx = await sut.ExportAsync(pdf.ToArray());

        using var ms = new MemoryStream(docx);
        using var word = WordprocessingDocument.Open(ms, false);
        var text = word.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Hello converted world", text);
        Assert.Contains("Second paragraph here", text);
        Assert.Equal(2, word.MainDocumentPart.Document.Body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Count());
    }
}
