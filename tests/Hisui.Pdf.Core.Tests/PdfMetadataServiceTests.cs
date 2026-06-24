using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfMetadataServiceTests
{
    private readonly PdfMetadataService _sut = new();

    [Fact]
    public async Task ReadAsync_OnFreshFixturePdf_ReturnsNullFields()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var meta = await _sut.ReadAsync(pdf);
        Assert.Null(meta.Title);
        Assert.Null(meta.Author);
    }

    [Fact]
    public async Task WriteAsync_Title_CanBeReadBack()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var written = await _sut.WriteAsync(pdf, new PdfMetadata { Title = "Hello PDF" });
        var meta = await _sut.ReadAsync(written);
        Assert.Equal("Hello PDF", meta.Title);
    }

    [Fact]
    public async Task WriteAsync_AllFields_RoundTrip()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var input = new PdfMetadata
        {
            Title    = "My Title",
            Author   = "Jane Doe",
            Subject  = "Testing",
            Keywords = "pdf test",
            Creator  = "Hisui.PDF",
        };

        var written = await _sut.WriteAsync(pdf, input);
        var meta = await _sut.ReadAsync(written);

        Assert.Equal(input.Title,    meta.Title);
        Assert.Equal(input.Author,   meta.Author);
        Assert.Equal(input.Subject,  meta.Subject);
        Assert.Equal(input.Keywords, meta.Keywords);
        Assert.Equal(input.Creator,  meta.Creator);
    }

    [Fact]
    public async Task WriteAsync_NullFieldDoesNotOverwriteExisting()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var withTitle = await _sut.WriteAsync(pdf, new PdfMetadata { Title = "Keep me" });
        // Write with null Title → should leave existing value
        var result = await _sut.WriteAsync(withTitle, new PdfMetadata { Author = "New Author" });
        var meta = await _sut.ReadAsync(result);

        Assert.Equal("Keep me",    meta.Title);
        Assert.Equal("New Author", meta.Author);
    }
}
