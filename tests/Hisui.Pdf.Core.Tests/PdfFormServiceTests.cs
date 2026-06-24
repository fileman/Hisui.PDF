using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfFormServiceTests
{
    private readonly PdfFormService _sut = new(new PdfiumRenderer());

    [Fact]
    public async Task ReadFields_OnNonFormPdf_ReturnsEmptyList()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var fields = await _sut.ReadFieldsAsync(pdf);
        Assert.Empty(fields);
    }

    [Fact]
    public async Task FillFields_OnNonFormPdf_ReturnsOriginalReference()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        // No values supplied → short-circuit before even opening the document.
        var result = await _sut.FillFieldsAsync(pdf, new Dictionary<string, string>());
        Assert.Same(pdf, result);
    }

    [Fact]
    public async Task FlattenAsync_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        var flattened = await _sut.FlattenAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(flattened), PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task FlattenAsync_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.FlattenAsync(pdf);
        Assert.True(result[0] == '%' && result[1] == 'P' && result[2] == 'D' && result[3] == 'F');
    }
}
