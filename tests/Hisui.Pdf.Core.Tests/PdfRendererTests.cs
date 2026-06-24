using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfRendererTests
{
    private readonly PdfiumRenderer _sut = new();

    [Fact]
    public async Task GetPageCount_MatchesDocument()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        Assert.Equal(3, await _sut.GetPageCountAsync(pdf));
    }

    [Fact]
    public async Task RenderPageToPng_ReturnsPngBytes()
    {
        var pdf = PdfFixtureBuilder.Create(2);

        var png = await _sut.RenderPageToPngAsync(pdf, pageIndex: 1, dpi: 96);

        Assert.NotNull(png);
        Assert.True(png.Length > 0);
        // PNG signature: 89 50 4E 47.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }
}
