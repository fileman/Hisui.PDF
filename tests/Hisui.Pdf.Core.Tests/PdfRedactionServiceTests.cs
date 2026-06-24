using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfRedactionServiceTests
{
    private readonly PdfiumRenderer _renderer = new();
    private PdfRedactionService Sut => new(_renderer);

    private static bool IsValidPdf(byte[] bytes) =>
        bytes.Length > 4 &&
        bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    [Fact]
    public async Task Redact_WithNoRegions_ReturnsOriginalBytes()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var result = await Sut.RedactAsync(pdf, []);
        Assert.Same(pdf, result); // should be the identical reference (short-circuit)
    }

    [Fact]
    public async Task Redact_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        IReadOnlyList<(int, PdfRect)> regions = [(1, new PdfRect(0.1, 0.1, 0.9, 0.9))];
        var result = await Sut.RedactAsync(pdf, regions);
        Assert.Equal(3, PageCount(result));
    }

    [Fact]
    public async Task Redact_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        IReadOnlyList<(int, PdfRect)> regions = [(0, new PdfRect(0.2, 0.2, 0.8, 0.8))];
        var result = await Sut.RedactAsync(pdf, regions);
        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task Redact_MultipleRegionsSamePage_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        IReadOnlyList<(int, PdfRect)> regions =
        [
            (0, new PdfRect(0.0, 0.0, 0.5, 0.1)),
            (0, new PdfRect(0.0, 0.9, 1.0, 1.0)),
        ];
        var result = await Sut.RedactAsync(pdf, regions);
        Assert.True(IsValidPdf(result));
        Assert.Equal(1, PageCount(result));
    }

    [Fact]
    public async Task Redact_OutOfRangePage_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        IReadOnlyList<(int, PdfRect)> regions = [(5, new PdfRect(0, 0, 1, 1))];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Sut.RedactAsync(pdf, regions));
    }
}
