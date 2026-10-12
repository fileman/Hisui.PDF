using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfSignatureServiceTests
{
    private readonly PdfSignatureService _sut = new(new HttpClient());

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Ada Lovelace, O=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task Sign_ProducesValidPadesSignature_AndTamperingIsDetected()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Contratto", 2);
        var rect = new PdfRect(0.6, 0.85, 0.95, 0.95);

        var signed = await _sut.SignAsync(pdf, new SignRequest { Certificate = SelfSigned(), PageIndex = 1, Rect = rect, Reason = "Approvazione", Location = "Ancona" });

        var info = Assert.Single(await _sut.VerifyAsync(signed));
        Assert.Equal("ETSI.CAdES.detached", info.SubFilter);
        Assert.True(info.IntegrityOk, info.Problem);
        Assert.True(info.CoversWholeDocument);
        Assert.False(info.CertificateTrusted); // self-signed
        Assert.Equal("Ada Lovelace", info.SignerName);
        Assert.Equal("Approvazione", info.Reason);
        Assert.Contains("ETSI.CAdES.detached", Encoding.Latin1.GetString(signed));

        // The visible widget sits where it was asked to.
        using (var doc = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import))
        {
            var widget = doc.Pages[1].Elements.GetArray("/Annots")!.Elements
                .Select(e => e is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : e).OfType<PdfDictionary>()
                .Single(d => d.Elements.GetName("/Subtype") == "/Widget");
            var raw = widget.Elements.GetRectangle("/Rect");
            var h = doc.Pages[1].Height.Point;
            var w = doc.Pages[1].Width.Point;
            Assert.Equal(rect.Left, raw.X1 / w, 2);
            Assert.Equal(rect.Top, (h - raw.Y2) / h, 2);
        }
        // …and its appearance is actually drawn there (ink inside the box that the unsigned page lacks).
        using var before = SkiaSharp.SKBitmap.Decode(await new PdfiumRenderer().RenderPageToPngAsync(pdf, 1, 72));
        using var after = SkiaSharp.SKBitmap.Decode(await new PdfiumRenderer().RenderPageToPngAsync(signed, 1, 72));
        var changed = 0;
        for (var y = (int)(rect.Top * after.Height); y < (int)(rect.Bottom * after.Height); y++)
            for (var x = (int)(rect.Left * after.Width); x < (int)(rect.Right * after.Width); x++)
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;
        Assert.True(changed > 50, $"only {changed} pixels changed in the signature box");

        // Changing one signed byte breaks the integrity check.
        var tampered = (byte[])signed.Clone();
        var at = Encoding.Latin1.GetString(tampered).IndexOf("Hisui PDF", StringComparison.Ordinal); // in the Info dictionary
        tampered[at] = (byte)'h';
        Assert.False(Assert.Single(await _sut.VerifyAsync(tampered)).IntegrityOk);
    }

    [Fact]
    public async Task Invisible_SignatureAndNoSignatures()
    {
        Assert.Empty(await _sut.VerifyAsync(PdfFixtureBuilder.Create(1)));
        var signed = await _sut.SignAsync(PdfFixtureBuilder.Create(1), new SignRequest { Certificate = SelfSigned() });
        Assert.True(Assert.Single(await _sut.VerifyAsync(signed)).IntegrityOk);
    }
}
