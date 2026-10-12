using System.Globalization;
using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Preflight and conversion for PDF versions, PDF/A-1/2/3 (b, u), PDF/UA-1 and PDF/X-4. The checks cover the rules
/// that can be verified structurally (fonts, transparency, actions, metadata, output intent, annotations, tagging…).
/// ponytail: not a certified validator (veraPDF checks hundreds of rules, e.g. glyph widths and content-stream operators);
/// what cannot be repaired without re-authoring (missing fonts, tags, CMYK output intent) is reported, not faked.
/// </summary>
internal sealed class PdfConformanceService : IPdfConformanceService
{
    private static readonly HashSet<string> ForbiddenActions =
        ["/JavaScript", "/Launch", "/Sound", "/Movie", "/ResetForm", "/ImportData", "/Hide", "/SetOCGState", "/Rendition", "/Trans", "/GoTo3DView", "/GoToE"];

    private readonly IPdfFormService _forms;

    public PdfConformanceService(IPdfFormService forms) => _forms = forms;

    public Task<ConformanceReport> CheckAsync(byte[] pdf, PdfProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            var issues = Analyze(doc, profile, fix: false, new ConversionOptions(), ct);
            if (profile == PdfProfile.PdfUA1) issues.AddRange(TextlessPages(pdf));
            return new ConformanceReport(profile, issues);
        }, ct);
    }

    public async Task<(byte[] Pdf, ConformanceReport Remaining)> ConvertAsync(byte[] pdf, PdfProfile profile, ConversionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        // PDF/A forbids /NeedAppearances: burn the field values in rather than ship blank-looking fields.
        if ((profile.IsPdfA() || profile == PdfProfile.PdfX4) && await Task.Run(() => NeedsAppearances(pdf), ct))
            pdf = await _forms.FlattenFieldsAsync(pdf, null, ct);

        var converted = await Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            Analyze(doc, profile, fix: true, options, ct);
            doc.Version = profile.Version();
            // PDFsharp regenerates the XMP packet on every save; SetPdfA makes it add the sRGB output intent and a
            // PDF/A identification block, which is then rewritten for the actual profile.
            var identify = profile.IsPdfA() || profile is PdfProfile.PdfUA1 or PdfProfile.PdfX4;
            if (identify)
            {
                // SetPdfA also starts PDFsharp's tagging support, which marks the document as tagged with an empty
                // structure tree: an untagged document must not claim to be tagged.
                var catalog = doc.Internals.Catalog.Elements;
                var wasTagged = (catalog.ContainsKey("/MarkInfo"), catalog.ContainsKey("/StructTreeRoot"));
                doc.SetPdfA();
                if (!wasTagged.Item1) catalog.Remove("/MarkInfo");
                if (!wasTagged.Item2) catalog.Remove("/StructTreeRoot");
            }
            using var output = new MemoryStream();
            doc.SaveStamped(output);
            var bytes = output.ToArray();
            return identify ? SetXmpIdentification(bytes, profile) : bytes;
        }, ct);
        return (converted, await CheckAsync(converted, profile, ct));
    }

    private static bool NeedsAppearances(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetBoolean("/NeedAppearances") == true;
    }

    // ── the rules ────────────────────────────────────────────────────────────

    private static List<ConformanceIssue> Analyze(PdfDocument doc, PdfProfile profile, bool fix, ConversionOptions options, CancellationToken ct)
    {
        var issues = new List<ConformanceIssue>();
        void Add(IssueSeverity s, string code, string detail, bool fixable) => issues.Add(new(s, code, detail, fixable));
        var catalog = doc.Internals.Catalog;
        var isA = profile.IsPdfA();
        var part = profile.PdfAPart();
        var isUA = profile == PdfProfile.PdfUA1;
        var isX = profile == PdfProfile.PdfX4;

        if (profile is PdfProfile.Pdf14 or PdfProfile.Pdf15 or PdfProfile.Pdf16 or PdfProfile.Pdf17 or PdfProfile.Pdf20)
        {
            if (doc.Version != profile.Version())
                Add(IssueSeverity.Info, "Version", $"{doc.Version / 10}.{doc.Version % 10} → {profile.DisplayName()}", true);
            return issues; // a version change has no further requirements here
        }

        // Version ceiling.
        if (doc.Version > profile.Version())
            Add(IssueSeverity.Error, "VersionTooHigh", $"{doc.Version / 10}.{doc.Version % 10}", true);

        var walker = new ResourceWalker(ct);
        for (var p = 0; p < doc.PageCount; p++) walker.Page(doc.Pages[p], p + 1);

        // Fonts must be embedded (PDF/A, PDF/UA, PDF/X).
        foreach (var (font, pageNo) in walker.Fonts)
        {
            if (font.Elements.GetName("/Subtype") == "/Type3") continue;
            var descriptorOwner = font.Elements.GetName("/Subtype") == "/Type0" &&
                                  Resolve(font.Elements["/DescendantFonts"]) is PdfArray { Elements.Count: > 0 } kids
                ? Resolve(kids.Elements[0]) as PdfDictionary
                : font;
            var fd = Resolve(descriptorOwner?.Elements["/FontDescriptor"]) as PdfDictionary;
            if (fd is null || !(fd.Elements.ContainsKey("/FontFile") || fd.Elements.ContainsKey("/FontFile2") || fd.Elements.ContainsKey("/FontFile3")))
                Add(IssueSeverity.Error, "FontNotEmbedded", $"{font.Elements.GetName("/BaseFont").TrimStart('/')} (p. {pageNo})", false);
            if ((part > 1 && profile.PdfALevel() == "U" || isUA) && !font.Elements.ContainsKey("/ToUnicode") && font.Elements.GetName("/Subtype") is "/Type0")
                Add(IssueSeverity.Warning, "NoToUnicode", font.Elements.GetName("/BaseFont").TrimStart('/'), false);
        }

        // Transparency is forbidden in PDF/A-1.
        if (part == 1)
        {
            foreach (var detail in walker.Transparency)
                Add(IssueSeverity.Error, "Transparency", detail, false);
            // The page group alone only changes how transparent content blends; with none left it can go.
            foreach (var (page, number) in walker.PageGroups)
            {
                Add(IssueSeverity.Error, "TransparencyGroup", $"p. {number}", true);
                if (fix) page.Elements.Remove("/Group");
            }
        }

        // LZW (PDF/A) and optional content (PDF/A-1).
        if (isA)
            foreach (var detail in walker.Lzw) Add(IssueSeverity.Error, "Lzw", detail, false);
        if (part == 1 && catalog.Elements.ContainsKey("/OCProperties"))
            Add(IssueSeverity.Error, "OptionalContent", "", false);

        // Forbidden actions and JavaScript.
        if (isA || isX)
        {
            foreach (var (owner, key, detail) in walker.Actions(catalog, doc))
            {
                if (!ForbiddenAction(Resolve(owner.Elements[key]))) continue;
                Add(IssueSeverity.Error, "ForbiddenAction", detail, true);
                if (fix) owner.Elements.Remove(key);
            }
            foreach (var (owner, detail) in walker.AdditionalActions(catalog, doc))
            {
                Add(IssueSeverity.Error, "AdditionalActions", detail, true);
                if (fix) owner.Elements.Remove("/AA");
            }
            if (catalog.Elements.GetDictionary("/Names") is { } names && names.Elements.ContainsKey("/JavaScript"))
            {
                Add(IssueSeverity.Error, "DocumentJavaScript", "", true);
                if (fix) names.Elements.Remove("/JavaScript");
            }
        }

        // Embedded files: none in A-1, only PDF/A files in A-2 (not verifiable here → removed), A-3 needs /AFRelationship.
        if (isA && catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles") is { } ef)
        {
            if (part < 3)
            {
                Add(IssueSeverity.Error, "EmbeddedFiles", "", true);
                if (fix) catalog.Elements.GetDictionary("/Names")!.Elements.Remove("/EmbeddedFiles");
            }
            else
            {
                var specs = FileSpecs(ef).ToList();
                if (specs.Any(s => !s.Elements.ContainsKey("/AFRelationship")))
                {
                    Add(IssueSeverity.Error, "AfRelationship", "", true);
                    if (fix)
                    {
                        var af = new PdfArray(doc);
                        foreach (var s in specs)
                        {
                            if (!s.Elements.ContainsKey("/AFRelationship")) s.Elements.SetName("/AFRelationship", "/Unspecified");
                            af.Elements.Add(s.Reference ?? (PdfItem)s);
                        }
                        catalog.Elements["/AF"] = af;
                    }
                }
            }
        }

        // Annotations: printable, visible, with an appearance (PDF/A-2+: all but Popup/Link).
        if (isA || isX)
            foreach (var (annot, pageNo) in walker.Annotations)
            {
                var subtype = annot.Elements.GetName("/Subtype");
                if (subtype == "/Popup") continue;
                var f = annot.Elements.GetInteger("/F");
                if ((f & 4) == 0 || (f & (1 | 2 | 32)) != 0)
                {
                    Add(IssueSeverity.Error, "AnnotationFlags", $"{subtype.TrimStart('/')} (p. {pageNo})", true);
                    if (fix) annot.Elements.SetInteger("/F", (f | 4) & ~(1 | 2 | 32));
                }
                if (part >= 2 && subtype != "/Link" && annot.Elements.GetDictionary("/AP")?.Elements["/N"] is null)
                {
                    var fixable = subtype == "/Text";
                    Add(IssueSeverity.Error, "AnnotationAppearance", $"{subtype.TrimStart('/')} (p. {pageNo})", fixable);
                    if (fix && fixable) NoteAppearance(doc, annot);
                }
            }
        if ((isA || isX) && catalog.Elements.GetDictionary("/AcroForm") is { } acro && acro.Elements.GetBoolean("/NeedAppearances"))
        {
            Add(IssueSeverity.Error, "NeedAppearances", "", true);
            if (fix) acro.Elements.Remove("/NeedAppearances");
        }

        // Output intent (colour): PDF/A gets sRGB; PDF/X needs the printer's CMYK profile, which only the user can supply.
        var intents = Resolve(catalog.Elements["/OutputIntents"]) as PdfArray;
        bool HasIntent(string s) => intents?.Elements.Select(Resolve).OfType<PdfDictionary>().Any(i => i.Elements.GetName("/S") == s) == true;
        if (isA && !HasIntent("/GTS_PDFA1"))
            Add(IssueSeverity.Error, "OutputIntent", "sRGB", true); // added on save (SetPdfA)
        if (isX && !HasIntent("/GTS_PDFX"))
            Add(IssueSeverity.Error, "PrintOutputIntent", "", false);

        // PDF/X: every page needs a TrimBox or ArtBox.
        if (isX)
            for (var p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                if (page.Elements.ContainsKey("/TrimBox") || page.Elements.ContainsKey("/ArtBox")) continue;
                Add(IssueSeverity.Error, "TrimBox", $"p. {p + 1}", true);
                if (fix) page.Elements["/TrimBox"] = (page.Elements["/CropBox"] ?? page.Elements["/MediaBox"])!.Clone();
            }

        // Accessibility (PDF/UA): tags, language, title, tab order, field descriptions.
        if (isUA)
        {
            var tagged = catalog.Elements.GetDictionary("/MarkInfo")?.Elements.GetBoolean("/Marked") == true && catalog.Elements.ContainsKey("/StructTreeRoot");
            if (!tagged) Add(IssueSeverity.Error, "NotTagged", "", false);
            if (catalog.Elements.GetString("/Lang").Length == 0)
            {
                Add(IssueSeverity.Error, "NoLanguage", "", true);
                if (fix) catalog.Elements.SetString("/Lang", options.Language);
            }
            if (string.IsNullOrWhiteSpace(doc.Info.Title))
            {
                Add(IssueSeverity.Error, "NoTitle", "", true);
                if (fix) doc.Info.Title = string.IsNullOrWhiteSpace(options.Title) ? "Documento" : options.Title.Trim();
            }
            var prefs = catalog.Elements.GetDictionary("/ViewerPreferences");
            if (prefs?.Elements.GetBoolean("/DisplayDocTitle") != true)
            {
                Add(IssueSeverity.Error, "DisplayDocTitle", "", true);
                if (fix) doc.ViewerPreferences.DisplayDocTitle = true; // typed: PDFsharp's own UA code reads it back as such
            }
            for (var p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                if (page.Elements.GetArray("/Annots") is not { Elements.Count: > 0 } || page.Elements.GetName("/Tabs") == "/S") continue;
                Add(IssueSeverity.Error, "TabOrder", $"p. {p + 1}", true);
                if (fix) page.Elements.SetName("/Tabs", "/S");
            }
            foreach (var (annot, pageNo) in walker.Annotations.Where(a => a.Annot.Elements.GetName("/Subtype") == "/Widget"))
            {
                var field = annot;
                while (field.Elements.GetString("/T").Length == 0 && Resolve(field.Elements["/Parent"]) is PdfDictionary parent) field = parent;
                if (field.Elements.GetString("/TU").Length > 0) continue;
                Add(IssueSeverity.Warning, "FieldDescription", $"{field.Elements.GetString("/T")} (p. {pageNo})", true);
                if (fix) field.Elements.SetString("/TU", field.Elements.GetString("/T").Replace('_', ' '));
            }
            if (!tagged)
                Add(IssueSeverity.Info, "AltText", "", false); // image alternate text lives in the structure tree
        }

        // XMP metadata identifying the standard (and matching the Info dictionary).
        if (isA || isUA || isX)
        {
            var xmp = Resolve(catalog.Elements["/Metadata"]) is PdfDictionary meta && meta.Stream is { } s ? Encoding.UTF8.GetString(s.UnfilteredValue) : "";
            var marker = isA ? $"pdfaid:part>{part}<|pdfaid:part=\"{part}\"" : isUA ? "pdfuaid:part" : "GTS_PDFXVersion";
            if (!marker.Split('|').Any(xmp.Contains))
                Add(IssueSeverity.Error, "Xmp", profile.DisplayName(), true); // written on save
        }
        return issues;
    }

    private static bool ForbiddenAction(PdfItem? action)
    {
        for (var a = action as PdfDictionary; a is not null; a = Resolve(a.Elements["/Next"]) as PdfDictionary)
            if (ForbiddenActions.Contains(a.Elements.GetName("/S"))) return true;
        return false;
    }

    private static IEnumerable<PdfDictionary> FileSpecs(PdfDictionary nameTree)
    {
        if (nameTree.Elements.GetArray("/Names") is { } names)
            for (var i = 1; i < names.Elements.Count; i += 2)
                if (Resolve(names.Elements[i]) is PdfDictionary spec) yield return spec;
        if (nameTree.Elements.GetArray("/Kids") is { } kids)
            foreach (var kid in kids.Elements.Select(Resolve).OfType<PdfDictionary>())
                foreach (var spec in FileSpecs(kid)) yield return spec;
    }

    /// <summary>Pages with no extractable text (scans) cannot be read by assistive technology without OCR.</summary>
    private static IEnumerable<ConformanceIssue> TextlessPages(byte[] pdf)
    {
        var result = new List<ConformanceIssue>();
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
            foreach (var page in doc.GetPages())
                if (page.Letters.Count == 0) result.Add(new(IssueSeverity.Warning, "NoText", $"p. {page.Number}", false));
        }
        catch (Exception) { /* unreadable by PdfPig: skip this hint */ }
        return result;
    }

    // ── fixes ────────────────────────────────────────────────────────────────

    /// <summary>A yellow note icon for a sticky note without appearance.</summary>
    private static void NoteAppearance(PdfDocument doc, PdfDictionary annot)
    {
        var r = annot.Elements.GetRectangle("/Rect");
        var (w, h) = (Math.Max(1, r.Width), Math.Max(1, r.Height));
        var f = CultureInfo.InvariantCulture;
        var content = string.Format(f, "1 0.82 0 rg 0.4 0.3 0 RG 0.8 w 0.5 0.5 {0:0.##} {1:0.##} re B 0.4 0.3 0 RG {2:0.##} {3:0.##} m {4:0.##} {3:0.##} l S {2:0.##} {5:0.##} m {4:0.##} {5:0.##} l S",
            w - 1, h - 1, w * 0.2, h * 0.65, w * 0.8, h * 0.4);
        var xobj = new PdfDictionary(doc);
        xobj.Elements.SetName("/Type", "/XObject");
        xobj.Elements.SetName("/Subtype", "/Form");
        xobj.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(w), new PdfReal(h));
        xobj.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(xobj);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = xobj.Reference;
        annot.Elements["/AP"] = ap;
    }

    /// <summary>
    /// Replaces PDFsharp's fixed PDF/A-1a identification block in the saved XMP with the target profile's, padded with
    /// spaces to the same byte length so no object offset moves (the XMP stream is written uncompressed).
    /// </summary>
    internal static byte[] SetXmpIdentification(byte[] pdf, PdfProfile profile)
    {
        var text = Encoding.Latin1.GetString(pdf);
        const string start = "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">";
        var at = text.LastIndexOf(start, StringComparison.Ordinal);
        if (at < 0) return pdf;
        var end = text.IndexOf("</rdf:Description>", at, StringComparison.Ordinal) + "</rdf:Description>".Length;
        var block = profile.IsPdfA()
            ? $"<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"><pdfaid:part>{profile.PdfAPart()}</pdfaid:part><pdfaid:conformance>{profile.PdfALevel()}</pdfaid:conformance></rdf:Description>"
            : profile == PdfProfile.PdfUA1
                ? "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>1</pdfuaid:part></rdf:Description>"
                : "<rdf:Description rdf:about=\"\" xmlns:pdfxid=\"http://www.npes.org/pdfx/ns/id/\"><pdfxid:GTS_PDFXVersion>PDF/X-4</pdfxid:GTS_PDFXVersion></rdf:Description>";
        if (block.Length > end - at) return pdf; // never grow: offsets must stay valid
        var patched = text[..at] + block.PadRight(end - at) + text[end..];
        return Encoding.Latin1.GetBytes(patched);
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>Collects fonts, transparency, LZW streams and annotations reachable from the pages (Form XObjects included).</summary>
    private sealed class ResourceWalker(CancellationToken ct)
    {
        private readonly HashSet<PdfDictionary> _seen = new(ReferenceEqualityComparer.Instance);
        public List<(PdfDictionary Font, int Page)> Fonts { get; } = [];
        public List<string> Transparency { get; } = [];
        /// <summary>Pages declaring a transparency group (PDFsharp adds one whenever it draws on a page).</summary>
        public List<(PdfPage Page, int Number)> PageGroups { get; } = [];
        public List<string> Lzw { get; } = [];
        public List<(PdfDictionary Annot, int Page)> Annotations { get; } = [];

        public void Page(PdfPage page, int number)
        {
            ct.ThrowIfCancellationRequested();
            if (Resolve(page.Elements["/Group"]) is PdfDictionary g && g.Elements.GetName("/S") == "/Transparency")
                PageGroups.Add((page, number));
            Stream(page, number);
            Resources(Resolve(page.Elements["/Resources"]) as PdfDictionary, number);
            foreach (var annot in (page.Elements.GetArray("/Annots")?.Elements.Select(Resolve) ?? []).OfType<PdfDictionary>())
            {
                Annotations.Add((annot, number));
                if (annot.Elements.GetDictionary("/AP") is { } ap)
                    foreach (var key in new[] { "/N", "/R", "/D" })
                        switch (Resolve(ap.Elements[key]))
                        {
                            case PdfDictionary { Stream: not null } x: XObject(x, number); break;
                            case PdfDictionary states:
                                foreach (var st in states.Elements.Values.Select(Resolve).OfType<PdfDictionary>()) XObject(st, number);
                                break;
                        }
            }
        }

        private void Resources(PdfDictionary? res, int page)
        {
            if (res is null || !_seen.Add(res)) return;
            foreach (var font in (Resolve(res.Elements["/Font"]) as PdfDictionary)?.Elements.Values.Select(Resolve).OfType<PdfDictionary>() ?? [])
                if (_seen.Add(font)) Fonts.Add((font, page));
            foreach (var gs in (Resolve(res.Elements["/ExtGState"]) as PdfDictionary)?.Elements.Values.Select(Resolve).OfType<PdfDictionary>() ?? [])
            {
                var smask = Resolve(gs.Elements["/SMask"]);
                if (smask is PdfDictionary || (smask is PdfName n && n.Value != "/None")) Transparency.Add($"SMask (p. {page})");
                foreach (var key in new[] { "/CA", "/ca" })
                    if (Resolve(gs.Elements[key]) is PdfReal { Value: < 1 } or PdfInteger { Value: < 1 }) Transparency.Add($"{key} (p. {page})");
                if (gs.Elements.GetName("/BM") is { Length: > 0 } bm && bm is not ("/Normal" or "/Compatible")) Transparency.Add($"{bm} (p. {page})");
            }
            foreach (var x in (Resolve(res.Elements["/XObject"]) as PdfDictionary)?.Elements.Values.Select(Resolve).OfType<PdfDictionary>() ?? [])
                XObject(x, page);
        }

        private void XObject(PdfDictionary x, int page)
        {
            if (!_seen.Add(x)) return;
            Stream(x, page);
            if (x.Elements.GetName("/Subtype") == "/Image" && x.Elements.ContainsKey("/SMask")) Transparency.Add($"image SMask (p. {page})");
            if (x.Elements.GetName("/Subtype") == "/Form")
            {
                if (Resolve(x.Elements["/Group"]) is PdfDictionary g && g.Elements.GetName("/S") == "/Transparency") Transparency.Add($"group (p. {page})");
                Resources(Resolve(x.Elements["/Resources"]) as PdfDictionary, page);
            }
        }

        private void Stream(PdfDictionary d, int page)
        {
            var filter = Resolve(d.Elements["/Filter"]);
            if (filter is PdfName { Value: "/LZWDecode" } || (filter is PdfArray a && a.Elements.Select(Resolve).OfType<PdfName>().Any(n => n.Value == "/LZWDecode")))
                Lzw.Add($"p. {page}");
        }

        /// <summary>Every /A and /OpenAction (catalog, outline, annotations) with a label.</summary>
        public IEnumerable<(PdfDictionary Owner, string Key, string Detail)> Actions(PdfDictionary catalog, PdfDocument doc)
        {
            if (catalog.Elements.ContainsKey("/OpenAction")) yield return (catalog, "/OpenAction", "OpenAction");
            foreach (var (annot, page) in Annotations)
                if (annot.Elements.ContainsKey("/A")) yield return (annot, "/A", $"{annot.Elements.GetName("/Subtype").TrimStart('/')} (p. {page})");
            var outline = catalog.Elements.GetDictionary("/Outlines");
            var stack = new Stack<PdfDictionary>();
            if (Resolve(outline?.Elements["/First"]) is PdfDictionary first) stack.Push(first);
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (!visited.Add(item)) continue;
                if (item.Elements.ContainsKey("/A")) yield return (item, "/A", "bookmark " + item.Elements.GetString("/Title"));
                if (Resolve(item.Elements["/Next"]) is PdfDictionary next) stack.Push(next);
                if (Resolve(item.Elements["/First"]) is PdfDictionary child) stack.Push(child);
            }
        }

        /// <summary>Every dictionary carrying /AA (catalog, pages, annotations, fields).</summary>
        public IEnumerable<(PdfDictionary Owner, string Detail)> AdditionalActions(PdfDictionary catalog, PdfDocument doc)
        {
            if (catalog.Elements.ContainsKey("/AA")) yield return (catalog, "document");
            for (var p = 0; p < doc.PageCount; p++)
                if (doc.Pages[p].Elements.ContainsKey("/AA")) yield return (doc.Pages[p], $"p. {p + 1}");
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var (annot, page) in Annotations)
                for (PdfDictionary? d = annot; d is not null && seen.Add(d); d = Resolve(d.Elements["/Parent"]) as PdfDictionary)
                    if (d.Elements.ContainsKey("/AA")) yield return (d, $"{d.Elements.GetString("/T")} (p. {page})");
        }
    }
}
