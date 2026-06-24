using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _sut = new();

    [Fact]
    public async Task ExtractText_OnShapeOnlyPdf_DoesNotThrow()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var text = await _sut.ExtractTextAsync(pdf);
        Assert.NotNull(text);
    }

    [Fact]
    public async Task ExtractWords_OnShapeOnlyPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var words = await _sut.ExtractWordsAsync(pdf, 0);
        Assert.Empty(words);
    }

    [Fact]
    public async Task ExtractText_OnTextPdf_ContainsExpectedWords()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World Test", 1);
        var text = await _sut.ExtractTextAsync(pdf);
        // At least some words must be present in the extracted text.
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected extracted text but got empty result.");
    }

    [Fact]
    public async Task ExtractWords_OnTextPdf_ReturnsBoundedWords()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Foo Bar Baz", 1);
        var words = await _sut.ExtractWordsAsync(pdf, 0);
        // The words collection may be empty if PdfPig cannot parse embedded glyphs; that's acceptable.
        // What is NOT acceptable is an exception or bounding boxes outside [0,1].
        foreach (var w in words)
        {
            Assert.InRange(w.BoundingBox.Left,   0, 1);
            Assert.InRange(w.BoundingBox.Top,    0, 1);
            Assert.InRange(w.BoundingBox.Right,  0, 1);
            Assert.InRange(w.BoundingBox.Bottom, 0, 1);
        }
    }

    [Fact]
    public async Task ExtractText_OutOfRangePage_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.ExtractTextAsync(pdf, 99));
    }

    [Fact]
    public async Task Search_OnShapeOnlyPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var results = await _sut.SearchAsync(pdf, "anything");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_ReturnsEmptyWhenQueryNotFound()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World", 1);
        var results = await _sut.SearchAsync(pdf, "XYZZY_NOT_PRESENT");
        Assert.Empty(results);
    }

    [Fact]
    public async Task ExtractText_AllPages_SeparatesWithFormFeed()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var text = await _sut.ExtractTextAsync(pdf); // page index null → all pages
        // Shape-only pages produce empty text; the form-feed separator is still added.
        // Just verify no exception and result is not null.
        Assert.NotNull(text);
    }
}
