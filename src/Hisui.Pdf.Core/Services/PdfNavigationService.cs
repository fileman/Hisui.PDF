using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

internal sealed class PdfNavigationService : IPdfNavigationService
{
    public Task<IReadOnlyList<PdfBookmark>> ReadOutlineAsync(byte[] pdf, CancellationToken ct = default) =>
        Read(pdf, ct, doc => (IReadOnlyList<PdfBookmark>)OutlineIO.Read(doc));

    public Task<byte[]> WriteOutlineAsync(byte[] pdf, IReadOnlyList<PdfBookmark> outline, CancellationToken ct = default) =>
        Modify(pdf, ct, doc =>
        {
            OutlineIO.Write(doc, outline, i => i >= 0 && i < doc.PageCount ? doc.Pages[i] : null);
            if (outline.Count > 0) doc.Internals.Catalog.Elements.SetName("/PageMode", "/UseOutlines");
        });

    public Task<IReadOnlyList<PdfLink>> ReadLinksAsync(byte[] pdf, CancellationToken ct = default) =>
        Read(pdf, ct, doc =>
        {
            var pages = OutlineIO.PageIndexMap(doc);
            var result = new List<PdfLink>();
            for (var p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                if (page.Elements.GetArray("/Annots") is not { } annots) continue;
                for (var i = 0; i < annots.Elements.Count; i++)
                {
                    if (OutlineIO.Resolve(annots.Elements[i]) is not PdfDictionary a || a.Elements.GetName("/Subtype") != "/Link") continue;
                    var action = OutlineIO.Resolve(a.Elements["/A"]) as PdfDictionary;
                    var uri = action?.Elements.GetName("/S") == "/URI" ? action.Elements.GetString("/URI") : null;
                    var dest = a.Elements["/Dest"] ?? OutlineIO.GoToDest(action);
                    var target = dest is null ? -1 : OutlineIO.DestPage(doc, dest, pages);
                    result.Add(new PdfLink
                    {
                        PageIndex = p,
                        AnnotIndex = i,
                        Rect = PageGeometry.ToVisual(page, a.Elements.GetRectangle("/Rect")),
                        TargetPage = target >= 0 ? target : null,
                        Uri = string.IsNullOrEmpty(uri) ? null : uri,
                    });
                }
            }
            return (IReadOnlyList<PdfLink>)result;
        });

    public Task<byte[]> AddLinkAsync(byte[] pdf, int pageIndex, PdfRect rect, int? targetPage, string? uri, CancellationToken ct = default)
    {
        if (targetPage is null == string.IsNullOrWhiteSpace(uri)) throw new ArgumentException("Give either a target page or a web address.");
        return Modify(pdf, ct, doc =>
        {
            var page = doc.Pages[pageIndex];
            var link = new PdfDictionary(doc);
            doc.Internals.AddObject(link);
            link.Elements.SetName("/Type", "/Annot");
            link.Elements.SetName("/Subtype", "/Link");
            link.Elements.SetRectangle("/Rect", PageGeometry.ToRaw(page, rect));
            link.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
            link.Elements.SetName("/H", "/I");
            link.Elements.SetInteger("/F", 4); // printable (PDF/A requires it)
            link.Elements["/P"] = page.Reference;
            if (targetPage is { } t)
            {
                if ((uint)t >= (uint)doc.PageCount) throw new ArgumentOutOfRangeException(nameof(targetPage));
                link.Elements["/Dest"] = new PdfArray(doc, doc.Pages[t].Reference!, new PdfName("/Fit"));
            }
            else
            {
                var action = new PdfDictionary(doc);
                action.Elements.SetName("/S", "/URI");
                action.Elements.SetString("/URI", uri!.Trim());
                link.Elements["/A"] = action;
            }
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            annots.Elements.Add(link.Reference!);
        });
    }

    public Task<byte[]> DeleteLinkAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default) =>
        Modify(pdf, ct, doc =>
        {
            var annots = doc.Pages[pageIndex].Elements.GetArray("/Annots");
            if (annots is null || (uint)annotIndex >= (uint)annots.Elements.Count ||
                OutlineIO.Resolve(annots.Elements[annotIndex]) is not PdfDictionary { } a || a.Elements.GetName("/Subtype") != "/Link")
                throw new ArgumentOutOfRangeException(nameof(annotIndex), "No link at that index.");
            annots.Elements.RemoveAt(annotIndex);
        });

    public Task<byte[]> StampAsync(byte[] pdf, IReadOnlyList<PageStamp> stamps, CancellationToken ct = default)
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        return Modify(pdf, ct, doc =>
        {
            foreach (var s in stamps)
            {
                ct.ThrowIfCancellationRequested();
                if ((uint)s.PageIndex >= (uint)doc.PageCount) continue;
                var page = doc.Pages[s.PageIndex];
                var (w, h) = PageGeometry.VisualSize(page);
                using var gfx = PageGeometry.OpenGraphics(page);
                var font = new XFont("Arial", s.FontSize);
                var brush = new XSolidBrush(XColor.FromArgb(Convert.ToInt32(s.ColorHex.TrimStart('#').PadLeft(6, '0')[..6], 16) | unchecked((int)0xFF000000)));
                var m = s.Margin;
                var lineH = s.FontSize * 1.3;
                void Draw(string text, XRect box, XStringFormat format)
                {
                    if (!string.IsNullOrWhiteSpace(text)) gfx.DrawString(text, font, brush, box, format);
                }
                Draw(s.TopLeft, new XRect(m, m, w - 2 * m, lineH), XStringFormats.TopLeft);
                Draw(s.TopCenter, new XRect(m, m, w - 2 * m, lineH), XStringFormats.TopCenter);
                Draw(s.TopRight, new XRect(m, m, w - 2 * m, lineH), XStringFormats.TopRight);
                Draw(s.BottomLeft, new XRect(m, h - m - lineH, w - 2 * m, lineH), XStringFormats.BottomLeft);
                Draw(s.BottomCenter, new XRect(m, h - m - lineH, w - 2 * m, lineH), XStringFormats.BottomCenter);
                Draw(s.BottomRight, new XRect(m, h - m - lineH, w - 2 * m, lineH), XStringFormats.BottomRight);
            }
        });
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static Task<T> Read<T>(byte[] pdf, CancellationToken ct, Func<PdfDocument, T> read)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify); // Modify: pages resolve to PdfPage instances
            return read(doc);
        }, ct);
    }

    private static Task<byte[]> Modify(byte[] pdf, CancellationToken ct, Action<PdfDocument> edit)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            edit(doc);
            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return output.ToArray();
        }, ct);
    }
}
