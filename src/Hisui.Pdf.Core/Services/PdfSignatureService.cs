using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// PAdES signatures (ETSI.CAdES.detached, SHA-256, ESS signing-certificate-v2, optional RFC 3161 time-stamp) on top of
/// PDFsharp's signature handler, plus verification of the signatures a document carries.
/// ponytail: PDFsharp saves the whole file (no incremental update), so signatures already present stop being valid —
/// the UI warns before signing such a document. No LTV (DSS / revocation data) yet.
/// </summary>
internal sealed class PdfSignatureService : IPdfSignatureService
{
    // PDFsharp writes adbe.pkcs7.detached; PAdES wants ETSI.CAdES.detached. Same length, so the swap moves no offset:
    // it is applied to the bytes being signed and then to the saved file, keeping both identical.
    private static readonly byte[] PdfSharpSubFilter = Encoding.ASCII.GetBytes("/adbe.pkcs7.detached");
    private static readonly byte[] PadesSubFilter = Encoding.ASCII.GetBytes("/ETSI.CAdES.detached");

    private readonly HttpClient _http;

    public PdfSignatureService(HttpClient http) => _http = http;

    public Task<byte[]> SignAsync(byte[] pdf, SignRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(request));
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();

        return Task.Run(() =>
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            var page = doc.Pages[request.PageIndex];
            var name = request.Certificate.GetNameInfo(X509NameType.SimpleName, false);

            var options = new DigitalSignatureOptions
            {
                Reason = request.Reason ?? "",
                Location = request.Location ?? "",
                ContactInfo = request.ContactInfo ?? "",
                AppName = "Hisui PDF",
                PageIndex = request.PageIndex,
                Rectangle = request.Rect is { } r ? SignatureRect(page, r) : new XRect(0, 0, 0, 0),
                AppearanceHandler = new Appearance(name, request.Reason, request.Location, request.Image),
            };
            DigitalSignatureHandler.ForDocument(doc, new PadesSigner(request.Certificate, request.TimestampServer, _http, ct), options);
            // Printable signature widget (PDF/A requires it); PDFsharp leaves /F unset.
            foreach (var widget in (page.Elements.GetArray("/Annots")?.Elements.Select(Resolve) ?? []).OfType<PdfDictionary>())
                if (widget.Elements.GetName("/FT") == "/Sig" && !widget.Elements.ContainsKey("/V")) widget.Elements.SetInteger("/F", 4);

            using var output = new MemoryStream();
            doc.SaveStamped(output);
            var bytes = output.ToArray();
            Replace(bytes, PdfSharpSubFilter, PadesSubFilter);
            return bytes;
        }, ct);
    }

    /// <summary>PDFsharp's signature rectangle is in raw page space with the origin at the bottom-left.</summary>
    private static XRect SignatureRect(PdfPage page, PdfRect visual)
    {
        var raw = PageGeometry.ToRaw(page, visual);
        return new XRect(raw.X1, raw.Y1, raw.Width, raw.Height);
    }

    internal static void Replace(byte[] data, byte[] find, byte[] with)
    {
        for (var i = data.AsSpan().IndexOf(find); i >= 0;)
        {
            with.CopyTo(data, i);
            var next = data.AsSpan(i + find.Length).IndexOf(find);
            i = next < 0 ? -1 : i + find.Length + next;
        }
    }

    // ── signer ───────────────────────────────────────────────────────────────

    private sealed class PadesSigner(X509Certificate2 cert, Uri? tsa, HttpClient http, CancellationToken ct) : IDigitalSigner
    {
        public string CertificateName => cert.GetNameInfo(X509NameType.SimpleName, false);

        // Room for the CMS with the whole chain (and the time-stamp token); unused space stays zero-padded.
        public Task<int> GetSignatureSizeAsync() => Task.FromResult(tsa is null ? 16_384 : 28_672);

        public async Task<byte[]> GetSignatureAsync(Stream stream)
        {
            // PDFsharp's RangedStream throws on CanSeek, which Stream.CopyTo queries: read it by hand.
            using var ms = new MemoryStream();
            var buffer = new byte[81_920];
            stream.Position = 0; // the handler hands the stream over positioned at its end
            for (int n; (n = stream.Read(buffer, 0, buffer.Length)) > 0;) ms.Write(buffer, 0, n);
            await Task.CompletedTask;
            var data = ms.ToArray();
            Replace(data, PdfSharpSubFilter, PadesSubFilter);

            var cms = new SignedCms(new ContentInfo(data), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
            {
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
                IncludeOption = X509IncludeOption.WholeChain,
            };
            // PAdES baseline: ESS signing-certificate-v2, and no CMS signing-time (the time is /M or the time-stamp).
            signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", SigningCertificateV2(cert)));
            cms.ComputeSignature(signer, silent: false); // smart cards may prompt for the PIN here

            if (tsa is not null)
            {
                var si = cms.SignerInfos[0];
                var request = Rfc3161TimestampRequest.CreateFromSignerInfo(si, HashAlgorithmName.SHA256, requestSignerCertificates: true,
                    nonce: RandomNumberGenerator.GetBytes(8));
                using var content = new ByteArrayContent(request.Encode());
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
                using var response = await http.PostAsync(tsa, content, ct);
                response.EnsureSuccessStatusCode();
                var token = request.ProcessResponse(await response.Content.ReadAsByteArrayAsync(ct), out _);
                si.AddUnsignedAttribute(new AsnEncodedData("1.2.840.113549.1.9.16.2.14", token.AsSignedCms().Encode()));
            }
            return cms.Encode();
        }

        /// <summary>DER of SigningCertificateV2 { certs: SEQUENCE OF ESSCertIDv2 { certHash (SHA-256 default, so no algorithm) } }.</summary>
        private static byte[] SigningCertificateV2(X509Certificate2 c)
        {
            var w = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (w.PushSequence())
            using (w.PushSequence())
            using (w.PushSequence())
                w.WriteOctetString(SHA256.HashData(c.RawData));
            return w.Encode();
        }
    }

    /// <summary>Visible signature: optional image on the left, signer / date / reason / location on the right.</summary>
    private sealed class Appearance(string signer, string? reason, string? location, byte[]? image) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            // PDFsharp passes the widget's page rectangle, but the graphics draw into the appearance's own box.
            rect = new XRect(0, 0, rect.Width, rect.Height);
            var text = rect;
            if (image is { Length: > 0 })
            {
                using var ms = new MemoryStream(image);
                using var img = XImage.FromStream(ms);
                var w = rect.Width * 0.45;
                var scale = Math.Min(w / img.PointWidth, rect.Height / img.PointHeight);
                var (iw, ih) = (img.PointWidth * scale, img.PointHeight * scale);
                gfx.DrawImage(img, rect.X + (w - iw) / 2, rect.Y + (rect.Height - ih) / 2, iw, ih);
                text = new XRect(rect.X + w + 4, rect.Y, rect.Width - w - 4, rect.Height);
            }
            var lines = new List<string> { "Firmato digitalmente da", signer, DateTime.Now.ToString("dd/MM/yyyy HH:mm") };
            if (!string.IsNullOrWhiteSpace(reason)) lines.Add(reason);
            if (!string.IsNullOrWhiteSpace(location)) lines.Add(location);
            var size = Math.Clamp(text.Height / (lines.Count * 1.25), 4, 10);
            var font = new XFont("Arial", size);
            for (var i = 0; i < lines.Count; i++)
                gfx.DrawString(lines[i], i == 1 ? new XFont("Arial", size, XFontStyleEx.Bold) : font, XBrushes.Black,
                    new XRect(text.X, text.Y + i * size * 1.25, text.Width, size * 1.25), XStringFormats.TopLeft);
        }
    }

    // ── verification ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<SignatureInfo>> VerifyAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<SignatureInfo>>(() =>
        {
            var result = new List<SignatureInfo>();
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields) return result;
            foreach (var (name, field) in SignatureFields(fields, ""))
            {
                ct.ThrowIfCancellationRequested();
                if (Resolve(field.Elements["/V"]) is not PdfDictionary sig) continue; // empty signature field
                result.Add(Check(pdf, name, sig));
            }
            return result;
        }, ct);
    }

    private static SignatureInfo Check(byte[] pdf, string name, PdfDictionary sig)
    {
        var info = new SignatureInfo
        {
            FieldName = name,
            Reason = NullIfEmpty(sig.Elements.GetString("/Reason")),
            Location = NullIfEmpty(sig.Elements.GetString("/Location")),
            SubFilter = sig.Elements.GetName("/SubFilter").TrimStart('/'),
            SigningTime = PdfCommentService.ParseDate(sig.Elements.GetString("/M")),
            SignerName = sig.Elements.GetString("/Name"),
        };
        if (Resolve(sig.Elements["/ByteRange"]) is not PdfArray { Elements.Count: 4 } br)
            return info with { Problem = "ByteRange" };
        var r = br.Elements.Select(e => Resolve(e) switch { PdfInteger i => (long)i.Value, PdfLongInteger l => l.Value, PdfReal d => (long)d.Value, _ => -1L }).ToArray();
        if (r.Any(v => v < 0) || r[0] + r[1] > pdf.Length || r[2] + r[3] > pdf.Length || r[2] < r[0] + r[1] + 2)
            return info with { Problem = "ByteRange" };
        info = info with { CoversWholeDocument = r[0] == 0 && r[2] + r[3] >= TrimmedLength(pdf) };

        try
        {
            // The gap between the two ranges is exactly the /Contents hex string, angle brackets included.
            var hex = Encoding.ASCII.GetString(pdf, (int)(r[0] + r[1] + 1), (int)(r[2] - r[0] - r[1] - 2));
            var der = TrimDer(Convert.FromHexString(Regex.Replace(hex, @"\s", "")));
            var signed = new byte[r[1] + r[3]];
            Array.Copy(pdf, r[0], signed, 0, r[1]);
            Array.Copy(pdf, r[2], signed, r[1], r[3]);

            var detached = info.SubFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached";
            var cms = detached ? new SignedCms(new ContentInfo(signed), detached: true) : new SignedCms();
            cms.Decode(der);
            var si = cms.SignerInfos[0];
            var cert = si.Certificate;
            var integrity = true;
            try { cms.CheckSignature(verifySignatureOnly: true); }
            catch (CryptographicException) { integrity = false; }
            if (!detached && integrity) // adbe.pkcs7.sha1: the CMS carries the SHA-1 of the ranges
                integrity = cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(signed));

            DateTime? stampTime = null;
            foreach (var attr in si.UnsignedAttributes)
                if (attr.Oid?.Value == "1.2.840.113549.1.9.16.2.14" && attr.Values.Count > 0 &&
                    Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _) &&
                    token.VerifySignatureForSignerInfo(si, out _))
                    stampTime = token.TokenInfo.Timestamp.LocalDateTime;

            string? problem = null;
            var trusted = false;
            if (cert is not null)
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // offline: no OCSP/CRL lookups
                chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                if (stampTime is { } t) chain.ChainPolicy.VerificationTime = t; // validity at signing time
                trusted = chain.Build(cert);
                if (!trusted) problem = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
            }
            return info with
            {
                SignerName = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? info.SignerName,
                Issuer = cert?.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                CertificateNotAfter = cert?.NotAfter,
                IntegrityOk = integrity,
                CertificateTrusted = trusted,
                HasTimestamp = stampTime is not null,
                SigningTime = stampTime ?? info.SigningTime,
                Problem = problem,
            };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return info with { Problem = ex.Message };
        }
    }

    /// <summary>File length without the trailing end-of-line a writer may add after %%EOF.</summary>
    private static long TrimmedLength(byte[] pdf)
    {
        var n = pdf.Length;
        while (n > 0 && pdf[n - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or 0) n--;
        return n;
    }

    /// <summary>The CMS is zero-padded inside /Contents: keep only the outer DER element.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x30) return data;
        int len, header;
        if (data[1] < 0x80) { len = data[1]; header = 2; }
        else
        {
            var count = data[1] & 0x7F;
            len = 0;
            for (var i = 0; i < count; i++) len = (len << 8) | data[2 + i];
            header = 2 + count;
        }
        return header + len <= data.Length ? data[..(header + len)] : data;
    }

    private static IEnumerable<(string Name, PdfDictionary Field)> SignatureFields(PdfArray fields, string prefix)
    {
        foreach (var f in fields.Elements.Select(Resolve).OfType<PdfDictionary>())
        {
            var t = f.Elements.GetString("/T");
            var name = t.Length == 0 ? prefix : prefix.Length == 0 ? t : prefix + "." + t;
            if (f.Elements.GetName("/FT") == "/Sig") yield return (name, f);
            if (f.Elements.GetArray("/Kids") is { } kids)
                foreach (var child in SignatureFields(kids, name)) yield return child;
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;
}
