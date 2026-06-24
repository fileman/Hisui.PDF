using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfImageExtractorTests
{
    private readonly PdfImageExtractor _sut = new();

    [Fact]
    public async Task ExtractImages_OnShapeOnlyPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var images = await _sut.ExtractImagesAsync(pdf);
        Assert.Empty(images);
    }

    [Fact]
    public async Task ExtractImages_SinglePage_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        var images = await _sut.ExtractImagesAsync(pdf, pageIndex: 1);
        Assert.Empty(images);
    }

    [Fact]
    public async Task ExtractImages_OutOfRangePage_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.ExtractImagesAsync(pdf, pageIndex: 99));
    }
}
