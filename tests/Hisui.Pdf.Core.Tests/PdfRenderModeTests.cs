using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfRenderModeTests : IDisposable
{
    private const int Dpi = 72; // 1 pixel per point keeps the region maths below trivial

    // Red text in the top band, a red filled box in the bottom band.
    private static readonly (int Top, int Bottom) TextBand = (60, 130);
    private static readonly (int Top, int Bottom) BoxBand = (400, 500);

    private readonly PdfiumRenderer _sut = new();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Color_KeepsColouredTextAndArt()
    {
        var page = await _sut.RenderPageAsync(CreateRedTextAndBox(), 0, Dpi, new PdfRenderOptions(PdfRenderColorMode.Color));

        Assert.True(CountColoured(page, TextBand) > 50);
        Assert.True(CountColoured(page, BoxBand) > 1000);
    }

    [Fact]
    public async Task Grayscale_LeavesNoColouredPixels()
    {
        var page = await _sut.RenderPageAsync(CreateRedTextAndBox(), 0, Dpi, new PdfRenderOptions(PdfRenderColorMode.Grayscale));

        Assert.Equal(0, CountColoured(page, (0, page.Height)));
        Assert.True(CountDark(page, BoxBand) > 1000); // the box is still there, just grey
    }

    [Fact]
    public async Task BlackText_BlackensTextOnly_AndDoesNotAlterTheDocument()
    {
        var pdf = CreateRedTextAndBox();

        var black = await _sut.RenderPageAsync(pdf, 0, Dpi, new PdfRenderOptions(PdfRenderColorMode.BlackText));
        Assert.Equal(0, CountColoured(black, TextBand));
        Assert.True(CountDark(black, TextBand) > 50);
        Assert.True(CountColoured(black, BoxBand) > 1000); // vector art keeps its colour

        // The recolouring lives on the transient page only: a later colour render is unchanged.
        var again = await _sut.RenderPageAsync(pdf, 0, Dpi, new PdfRenderOptions(PdfRenderColorMode.Color));
        Assert.True(CountColoured(again, TextBand) > 50);
    }

    [Fact]
    public async Task GetPageSizes_ReturnsPointsWithRotationApplied()
    {
        using var document = new PdfDocument();
        document.AddPage().Size = PdfSharp.PageSize.A4;
        var landscape = document.AddPage();
        landscape.Size = PdfSharp.PageSize.A4;
        landscape.Rotate = 90;
        using var stream = new MemoryStream();
        document.Save(stream);

        var sizes = await _sut.GetPageSizesAsync(stream.ToArray());

        Assert.Equal(2, sizes.Count);
        Assert.Equal(595, sizes[0].Width, 0);
        Assert.Equal(842, sizes[0].Height, 0);
        Assert.Equal(842, sizes[1].Width, 0);
        Assert.Equal(595, sizes[1].Height, 0);
    }

    private static byte[] CreateRedTextAndBox()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawString("RED HEADLINE", new XFont("Arial", 40), XBrushes.Red, new XPoint(50, 120));
            gfx.DrawRectangle(XBrushes.Red, 50, 410, 300, 80);
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    private static int CountColoured(RenderedPage page, (int Top, int Bottom) band) =>
        Count(page, band, (b, g, r) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60);

    private static int CountDark(RenderedPage page, (int Top, int Bottom) band) =>
        Count(page, band, (b, g, r) => r + g + b < 3 * 160);

    private static int Count(RenderedPage page, (int Top, int Bottom) band, Func<int, int, int, bool> match)
    {
        var count = 0;
        for (var y = Math.Max(0, band.Top); y < Math.Min(page.Height, band.Bottom); y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var i = y * page.Stride + x * 4;
                if (match(page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2])) count++;
            }
        }
        return count;
    }
}
