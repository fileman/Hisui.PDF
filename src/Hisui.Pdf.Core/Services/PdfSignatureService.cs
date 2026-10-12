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
/// an incremental update written by hand, plus verification of the signatures a document carries.
/// Signing appends an incremental update, so the signatures already in the file stay valid.
/// ponytail: no LTV (DSS / revocation data) and no certification (DocMDP) signatures yet.
/// </summary>
internal sealed class PdfSignatureService : IPdfSignatureService
{
    private const string ByteRangePlaceholder = "0 0000000000 0000000000 0000000000";

    private readonly HttpClient _http;

    public PdfSignatureService(HttpClient http) => _http = http;

    public Task<byte[]> SignAsync(byte[] pdf, SignRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Certificate.HasPrivateKey) throw new ArgumentException("The certificate has no private key.", nameof(request));
        GlobalFontSettings.FontResolver ??= new SystemFontResolver();

        return Task.Run(async () =>
        {
            try { return await SignIncrementalAsync(pdf, request, ct); }
            catch (InvalidDataException)
            {
                // Damaged cross-reference data: let PDFsharp rebuild the file, then append to that.
                using var input = new MemoryStream(pdf, writable: false);
                using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
                using var output = new MemoryStream();
                doc.Save(output);
                return await SignIncrementalAsync(output.ToArray(), request, ct);
            }
        }, ct);
    }

    public Task<IReadOnlyList<string>> ListEmptySignatureFieldsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { } fields
                ? SignatureFields(fields, "").Where(f => f.Field.Elements["/V"] is null).Select(f => f.Name).ToList()
                : [];
        }, ct);
    }

    private async Task<byte[]> SignIncrementalAsync(byte[] pdf, SignRequest request, CancellationToken ct)
    {
        var update = new PdfIncrementalUpdate(pdf);
        // Two readers: PDFsharp flattens the page tree as soon as Pages is touched, which rewrites /Parent and /Kids.
        // The objects written back come from `raw`, whose page tree is walked by hand; `geo` only measures pages.
        using var geoStream = new MemoryStream(pdf, writable: false);
        using var geo = PdfReader.Open(geoStream, PdfDocumentOpenMode.Import);
        using var rawStream = new MemoryStream(pdf, writable: false);
        using var raw = PdfReader.Open(rawStream, PdfDocumentOpenMode.Import);
        var catalog = raw.Internals.Catalog;
        var pages = PageTree(catalog).ToList();
        var signer = request.Certificate.GetNameInfo(X509NameType.SimpleName, false);

        PdfDictionary? field = null, widget = null;
        PdfDictionary page;
        PdfRectangle rect;
        if (request.FieldName is { } fieldName)
        {
            var fields = catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
            field = (fields is null ? null : SignatureFields(fields, "").FirstOrDefault(f => f.Name == fieldName && f.Field.Elements["/V"] is null).Field)
                ?? throw new ArgumentException($"No empty signature field named {fieldName}.", nameof(request));
            widget = field.Elements.GetName("/Subtype") == "/Widget" ? field
                : field.Elements.GetArray("/Kids")?.Elements.Select(Resolve).OfType<PdfDictionary>().FirstOrDefault()
                ?? throw new ArgumentException("The signature field has no widget.", nameof(request));
            page = PageOf(widget, pages) ?? throw new ArgumentException("The signature field is not on a page.", nameof(request));
            rect = widget.Elements.GetRectangle("/Rect");
        }
        else
        {
            page = pages[request.PageIndex];
            rect = request.Rect is { } r ? PageGeometry.ToRaw(geo.Pages[request.PageIndex], r) : new PdfRectangle();
        }

        var visible = rect.Width > 0 && rect.Height > 0;
        var appearance = WriteAppearance(update, Math.Abs(rect.Width), Math.Abs(rect.Height),
            visible ? new Appearance(signer, request.Reason, request.Location, request.Image) : null);

        // Signature dictionary with room for the CMS; /ByteRange and /Contents are filled in once the file is complete.
        var size = request.TimestampServer is null ? 16_384 : 28_672;
        var sigNum = update.Allocate();
        var now = DateTimeOffset.Now;
        var date = $"D:{now:yyyyMMddHHmmss}{(now.Offset < TimeSpan.Zero ? '-' : '+')}{now.Offset:hh}'{now.Offset:mm}'";
        var sig = new StringBuilder("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached /ByteRange [")
            .Append(ByteRangePlaceholder).Append("] /Contents <").Append('0', size * 2).Append("> /M ")
            .Append(PdfIncrementalUpdate.Str(date))
            .Append(" /Name ").Append(PdfIncrementalUpdate.Str(signer));
        if (request.Reason is { Length: > 0 } reason) sig.Append(" /Reason ").Append(PdfIncrementalUpdate.Str(reason));
        if (request.Location is { Length: > 0 } location) sig.Append(" /Location ").Append(PdfIncrementalUpdate.Str(location));
        if (request.ContactInfo is { Length: > 0 } contact) sig.Append(" /ContactInfo ").Append(PdfIncrementalUpdate.Str(contact));
        sig.Append(" /Prop_Build << /App << /Name /HisuiPDF >> >> >>");
        var sigText = sig.ToString();
        var sigAt = update.WriteObject(sigNum, 0, sigText);
        var byteRangeAt = sigAt + sigText.IndexOf(ByteRangePlaceholder, StringComparison.Ordinal);
        var contentsAt = sigAt + sigText.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;

        var form = catalog.Elements["/AcroForm"] is PdfReference formRef ? (PdfDictionary)formRef.Value : catalog.Elements["/AcroForm"] as PdfDictionary;
        if (form is null) catalog.Elements["/AcroForm"] = form = new PdfDictionary();
        form.Elements.SetInteger("/SigFlags", 3); // signatures exist, append only
        if (field is null)
        {
            // New field merged with its widget; printable (/F 4), as PDF/A requires.
            var fieldNum = update.Allocate();
            var name = UniqueFieldName(form);
            update.WriteObject(fieldNum, 0,
                $"<< /Type /Annot /Subtype /Widget /FT /Sig /T {PdfIncrementalUpdate.Str(name)} /F 4 " +
                $"/Rect {PdfIncrementalUpdate.Serialize(rect, PdfIncrementalUpdate.Identity)} /P {PdfIncrementalUpdate.Identity(page.Reference!)} " +
                $"/V {sigNum} 0 R /AP << /N {appearance} 0 R >> >>");
            var fieldRef = new PdfLiteral($"{fieldNum} 0 R");
            if (page.Elements["/Annots"] is PdfReference { Value: PdfArray sharedAnnots })
            {
                sharedAnnots.Elements.Add(fieldRef);
                update.Rewrite(sharedAnnots);
            }
            else
            {
                if (page.Elements["/Annots"] is not PdfArray annots) page.Elements["/Annots"] = annots = new PdfArray();
                annots.Elements.Add(fieldRef);
                update.Rewrite(page);
            }
            if (form.Elements["/Fields"] is PdfReference { Value: PdfArray sharedFields })
            {
                sharedFields.Elements.Add(fieldRef);
                update.Rewrite(sharedFields);
            }
            else
            {
                if (form.Elements["/Fields"] is not PdfArray list) form.Elements["/Fields"] = list = new PdfArray();
                list.Elements.Add(fieldRef);
            }
        }
        else
        {
            field.Elements["/V"] = new PdfLiteral($"{sigNum} 0 R");
            widget!.Elements["/AP"] = new PdfLiteral($"<< /N {appearance} 0 R >>");
            if (!widget.Elements.ContainsKey("/F")) widget.Elements.SetInteger("/F", 4);
            update.Rewrite(field);
            if (!ReferenceEquals(widget, field)) update.Rewrite(widget);
        }
        update.Rewrite(form.Reference is null ? catalog : form);

        // Sign everything but the /Contents hex string.
        var bytes = update.Finish();
        var a = (int)contentsAt;
        var b = a + size * 2 + 2;
        Encoding.ASCII.GetBytes($"0 {a:D10} {b:D10} {bytes.Length - b:D10}").CopyTo(bytes, byteRangeAt);
        var signed = new byte[a + bytes.Length - b];
        Array.Copy(bytes, 0, signed, 0, a);
        Array.Copy(bytes, b, signed, a, bytes.Length - b);
        var cms = await CreateCmsAsync(signed, request.Certificate, request.TimestampServer, ct);
        if (cms.Length > size) throw new InvalidOperationException("The signature does not fit the space reserved for it.");
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, a + 1);
        return bytes;
    }

    /// <summary>Leaf pages in document order, read without letting PDFsharp restructure the tree.</summary>
    private static IEnumerable<PdfDictionary> PageTree(PdfDictionary catalog)
    {
        var stack = new Stack<PdfDictionary>();
        if (Resolve(catalog.Elements["/Pages"]) is PdfDictionary root) stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Elements.GetName("/Type") == "/Page" || Resolve(node.Elements["/Kids"]) is not PdfArray kids) { yield return node; continue; }
            for (var i = kids.Elements.Count - 1; i >= 0; i--)
                if (Resolve(kids.Elements[i]) is PdfDictionary kid) stack.Push(kid);
        }
    }

    private static PdfDictionary? PageOf(PdfDictionary widget, List<PdfDictionary> pages)
    {
        if (widget.Elements["/P"] is PdfReference p && pages.FirstOrDefault(x => x.Reference?.ObjectNumber == p.ObjectNumber) is { } byP) return byP;
        var id = widget.Reference?.ObjectNumber;
        return pages.FirstOrDefault(pg => Resolve(pg.Elements["/Annots"]) is PdfArray annots &&
            annots.Elements.Any(e => e is PdfReference r ? r.ObjectNumber == id : ReferenceEquals(e, widget)));
    }

    private static string UniqueFieldName(PdfDictionary form)
    {
        var taken = (Resolve(form.Elements["/Fields"]) as PdfArray)?.Elements.Select(Resolve).OfType<PdfDictionary>()
            .Select(f => f.Elements.GetString("/T")).ToHashSet() ?? [];
        for (var n = 1; ; n++)
            if (!taken.Contains($"Firma{n}")) return $"Firma{n}";
    }

    /// <summary>
    /// Writes the signature's appearance (a form XObject) and returns its object number. PDFsharp draws it on a scratch
    /// page; the page's content becomes the form's stream and its resources (fonts, images) are copied over.
    /// ponytail: drawn upright in raw page space, so on a rotated page it looks rotated (no /MK /R handling).
    /// </summary>
    private static int WriteAppearance(PdfIncrementalUpdate update, double width, double height, Appearance? look)
    {
        var num = update.Allocate();
        if (look is null || width <= 0 || height <= 0)
        {
            update.WriteObject(num, 0, "<< /Type /XObject /Subtype /Form /BBox [0 0 0 0] /Length 0 >>", []);
            return num;
        }
        using var scratchOut = new MemoryStream();
        using (var scratch = new PdfDocument())
        {
            var p = scratch.AddPage();
            p.Width = XUnit.FromPoint(width);
            p.Height = XUnit.FromPoint(height);
            using (var gfx = XGraphics.FromPdfPage(p)) look.DrawAppearance(gfx, new XRect(0, 0, width, height));
            scratch.Save(scratchOut);
        }
        scratchOut.Position = 0;
        using var doc = PdfReader.Open(scratchOut, PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        var content = new MemoryStream();
        foreach (var part in page.Contents.Elements.Select(Resolve).OfType<PdfDictionary>())
        {
            content.Write(part.Stream.UnfilteredValue);
            content.WriteByte((byte)'\n');
        }

        // Objects of the scratch document get fresh numbers in the update, each written once.
        var numbers = new Dictionary<int, int>();
        var pending = new Queue<(int Num, PdfObject Obj)>();
        string Map(PdfReference r)
        {
            if (!numbers.TryGetValue(r.ObjectNumber, out var n))
            {
                numbers[r.ObjectNumber] = n = update.Allocate();
                pending.Enqueue((n, r.Value));
            }
            return $"{n} 0 R";
        }
        var resources = PdfIncrementalUpdate.Serialize(page.Elements["/Resources"], Map);
        update.WriteObject(num, 0,
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 {PdfIncrementalUpdate.Num(width)} {PdfIncrementalUpdate.Num(height)}] /Resources {resources} /Length {content.Length} >>",
            content.ToArray());
        while (pending.TryDequeue(out var item))
        {
            var (body, stream) = PdfIncrementalUpdate.Serialize(item.Obj, Map);
            update.WriteObject(item.Num, 0, body, stream);
        }
        return num;
    }

    // ── signer ───────────────────────────────────────────────────────────────

    /// <summary>Detached CAdES over the signed bytes: SHA-256, ESS signing-certificate-v2, optional RFC 3161 time-stamp.</summary>
    private async Task<byte[]> CreateCmsAsync(byte[] data, X509Certificate2 cert, Uri? tsa, CancellationToken ct)
    {
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
            using var response = await _http.PostAsync(tsa, content, ct);
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
