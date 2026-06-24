using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfTextEditServiceTests
{
    private readonly PdfTextEditService _sut = new();

    [Fact]
    public async Task ReplaceWord_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World");
        var bounds = new PdfRect(0.05, 0.1, 0.5, 0.15); // approximate normalized rect

        var result = await _sut.ReplaceWordAsync(pdf, 0, bounds, "Replaced", 14);

        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task ReplaceWord_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test", 2);
        var bounds = new PdfRect(0.05, 0.05, 0.3, 0.12);

        var result = await _sut.ReplaceWordAsync(pdf, 0, bounds, "New", 12);

        Assert.Equal(2, PageCount(result));
    }

    [Fact]
    public async Task ReplaceWord_ThrowsForOutOfRangePage()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test", 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sut.ReplaceWordAsync(pdf, 5, new PdfRect(0, 0, 0.1, 0.05), "x", 12));
    }

    [Fact]
    public async Task ReplaceWord_ThrowsForEmptyText()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.ReplaceWordAsync(pdf, 0, new PdfRect(0, 0, 0.1, 0.05), "", 12));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsValidPdf(byte[] bytes) =>
        bytes.Length > 4 &&
        bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }
}
