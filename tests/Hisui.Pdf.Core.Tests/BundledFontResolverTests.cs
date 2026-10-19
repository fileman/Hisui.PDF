using Hisui.Pdf.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class BundledFontResolverTests
{
    [Theory]
    [InlineData(false, false, "LiberationSans-Regular")]
    [InlineData(true, false, "LiberationSans-Bold")]
    [InlineData(false, true, "LiberationSans-Italic")]
    [InlineData(true, true, "LiberationSans-BoldItalic")]
    public void EveryFamily_ResolvesToTheBundledFace(bool bold, bool italic, string face)
    {
        var sut = new BundledFontResolver();
        foreach (var family in new[] { "Arial", "Times New Roman", "Anything" })
            Assert.Equal(face, sut.ResolveTypeface(family, bold, italic)!.FaceName);
        Assert.True(sut.GetFont(face)!.Length > 100_000);
    }

    [Fact]
    public void TextWrittenAsArial_EmbedsLiberationSans()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfDocument();
        using (var gfx = XGraphics.FromPdfPage(doc.AddPage()))
        {
            gfx.DrawString("Regular", new XFont("Arial", 12), XBrushes.Black, new XPoint(50, 100));
            gfx.DrawString("Bold", new XFont("Arial", 12, XFontStyleEx.Bold), XBrushes.Black, new XPoint(50, 130));
        }
        using var ms = new MemoryStream();
        doc.Save(ms);

        using var pig = UglyToad.PdfPig.PdfDocument.Open(ms.ToArray());
        var fonts = pig.GetPage(1).Letters.Select(l => l.FontName).Distinct().ToList();
        Assert.NotEmpty(fonts);
        Assert.All(fonts, f => Assert.Contains("Liberation Sans", f));
    }
}
