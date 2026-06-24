using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfSecurityServiceTests
{
    private readonly PdfSecurityService _sut = new();

    private static readonly PdfEncryptionOptions DefaultOptions = new()
    {
        UserPassword = "user123",
        OwnerPassword = "owner456",
    };

    [Fact]
    public void IsEncrypted_OnPlainPdf_ReturnsFalse()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        Assert.False(_sut.IsEncrypted(pdf));
    }

    [Fact]
    public async Task IsEncrypted_OnEncryptedPdf_ReturnsTrue()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var encrypted = await _sut.EncryptAsync(pdf, DefaultOptions);
        Assert.True(_sut.IsEncrypted(encrypted));
    }

    [Fact]
    public async Task EncryptAsync_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.EncryptAsync(pdf, DefaultOptions);
        Assert.True(result[0] == '%' && result[1] == 'P' && result[2] == 'D' && result[3] == 'F');
    }

    [Fact]
    public async Task DecryptAsync_RoundTrip_ProducesPlainPdf()
    {
        var original = PdfFixtureBuilder.Create(2);
        var encrypted = await _sut.EncryptAsync(original, DefaultOptions);
        var decrypted = await _sut.DecryptAsync(encrypted, DefaultOptions.UserPassword);
        Assert.False(_sut.IsEncrypted(decrypted));
    }

    [Fact]
    public async Task EncryptDecrypt_RoundTrip_PreservesPageCount()
    {
        const int pages = 3;
        var original = PdfFixtureBuilder.Create(pages);
        var encrypted = await _sut.EncryptAsync(original, DefaultOptions);
        var decrypted = await _sut.DecryptAsync(encrypted, DefaultOptions.OwnerPassword);

        using var doc = PdfReader.Open(new MemoryStream(decrypted), PdfDocumentOpenMode.Import);
        Assert.Equal(pages, doc.PageCount);
    }
}
