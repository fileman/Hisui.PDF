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
        var at = Encoding.Latin1.GetString(tampered).IndexOf("/HisuiPDF", StringComparison.Ordinal) + 1; // in the signature dictionary
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

    [Fact]
    public async Task SecondSignature_IsAppended_AndFirstStaysValid()
    {
        var first = await _sut.SignAsync(PdfFixtureBuilder.CreateWithText("Contratto", 2),
            new SignRequest { Certificate = SelfSigned(), PageIndex = 0, Rect = new PdfRect(0.05, 0.85, 0.4, 0.95), Reason = "Primo" });
        var second = await _sut.SignAsync(first,
            new SignRequest { Certificate = SelfSigned(), PageIndex = 1, Rect = new PdfRect(0.6, 0.85, 0.95, 0.95), Reason = "Secondo" });

        Assert.Equal(first, second[..first.Length]); // incremental: the first revision is untouched
        var infos = await _sut.VerifyAsync(second);
        Assert.Equal(2, infos.Count);
        Assert.All(infos, i => Assert.True(i.IntegrityOk, i.Problem));
        Assert.False(infos.Single(i => i.Reason == "Primo").CoversWholeDocument);
        Assert.True(infos.Single(i => i.Reason == "Secondo").CoversWholeDocument);
        Assert.Equal(["Firma1", "Firma2"], infos.Select(i => i.FieldName).Order());

        // Both widgets render and the file still opens cleanly in PDFsharp (and so its xref chain is sound).
        using var doc = PdfReader.Open(new MemoryStream(second), PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
        await new PdfiumRenderer().RenderPageToPngAsync(second, 1, 72);
    }

    [Fact]
    public async Task SignsIntoExistingEmptyField()
    {
        var forms = new PdfFormService(new PdfiumRenderer());
        var pdf = await forms.AddFieldAsync(PdfFixtureBuilder.Create(1), 0, new PdfRect(0.5, 0.3, 0.9, 0.4), PdfFormFieldType.Signature, "Approvatore");
        Assert.Equal(["Approvatore"], await _sut.ListEmptySignatureFieldsAsync(pdf));

        var signed = await _sut.SignAsync(pdf, new SignRequest { Certificate = SelfSigned(), FieldName = "Approvatore" });

        var info = Assert.Single(await _sut.VerifyAsync(signed));
        Assert.Equal("Approvatore", info.FieldName);
        Assert.True(info.IntegrityOk, info.Problem);
        Assert.True(info.CoversWholeDocument);
        Assert.Empty(await _sut.ListEmptySignatureFieldsAsync(signed));
    }

    /// <summary>A 2-page file with a cross-reference stream and a nested page tree (pages inherit /MediaBox).</summary>
    private static byte[] XrefStreamNestedTree()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 2 >>",
            "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 /MediaBox [0 0 595 842] >>",
            "<< /Type /Page /Parent 3 0 R /Contents 6 0 R >>",
            "<< /Type /Page /Parent 3 0 R >>",
            "<< /Length 25 >>\nstream\n10 10 m 200 200 l S 0 0 m\nendstream",
        ];
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefAt = sb.Length;
        offsets.Add(xrefAt);
        var data = new List<byte> { 0, 0, 0, 0xFF }; // object 0: free
        foreach (var o in offsets) data.AddRange([1, (byte)(o >> 8), (byte)o, 0]);
        sb.Append($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 2 1] /Root 1 0 R /Length {data.Count} >>\nstream\n");
        var bytes = new List<byte>(Encoding.Latin1.GetBytes(sb.ToString()));
        bytes.AddRange(data);
        bytes.AddRange(Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n"));
        return [.. bytes];
    }

    [Fact]
    public async Task XrefStreamAndNestedPageTree_StayConsistent()
    {
        var pdf = XrefStreamNestedTree();
        var once = await _sut.SignAsync(pdf, new SignRequest { Certificate = SelfSigned(), PageIndex = 1, Rect = new PdfRect(0.1, 0.1, 0.5, 0.2) });
        var twice = await _sut.SignAsync(once, new SignRequest { Certificate = SelfSigned(), PageIndex = 0, Rect = new PdfRect(0.1, 0.1, 0.5, 0.2) });

        Assert.All(await _sut.VerifyAsync(twice), i => Assert.True(i.IntegrityOk, i.Problem));
        Assert.Equal(2, (await _sut.VerifyAsync(twice)).Count);
        // Pages were rewritten as they were: still children of the intermediate node, nothing inherited copied in.
        var text = Encoding.Latin1.GetString(twice, pdf.Length, twice.Length - pdf.Length);
        Assert.Contains("5 0 obj\n<</Type /Page /Parent 3 0 R /Annots [", text);
        Assert.DoesNotContain("/MediaBox", text);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(twice);
        Assert.Equal(2, pig.NumberOfPages);
        Assert.Equal(2, pig.GetPage(2).ExperimentalAccess.GetAnnotations().Count() + pig.GetPage(1).ExperimentalAccess.GetAnnotations().Count());
    }
}
