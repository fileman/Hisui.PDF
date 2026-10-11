using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// PDFsharp page import carries widget annotations but not the catalog's /AcroForm, so a rebuilt document
/// would show the fields without them being fillable. This re-registers every widget's root field.
/// </summary>
internal static class AcroFormRestorer
{
    public static void Restore(PdfDocument doc)
    {
        var roots = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>();

        for (var p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (Resolve(annots.Elements[i]) is not PdfDictionary widget ||
                    widget.Elements.GetName("/Subtype") != "/Widget") continue;

                var field = widget;
                while (Resolve(field.Elements["/Parent"]) is PdfDictionary parent) field = parent;
                if (seen.Add(field)) roots.Add(field);
            }
        }
        if (roots.Count == 0) return;

        var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (acro is null)
        {
            acro = new PdfDictionary(doc);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro;
        }
        var fields = acro.Elements.GetArray("/Fields");
        if (fields is null)
        {
            fields = new PdfArray(doc);
            acro.Elements["/Fields"] = fields;
        }
        var known = new HashSet<PdfDictionary>();
        for (var i = 0; i < fields.Elements.Count; i++)
            if (Resolve(fields.Elements[i]) is PdfDictionary k) known.Add(k);

        var needAppearances = false;
        foreach (var root in roots)
        {
            if (!known.Contains(root)) fields.Elements.Add(EnsureIndirect(doc, root));
            var type = InheritedType(root);
            needAppearances |= type is "/Tx" or "/Ch";
        }

        EnsureHelvetica(doc, acro);
        if (acro.Elements.GetString("/DA") is not { Length: > 0 }) acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        if (needAppearances) acro.Elements.SetBoolean("/NeedAppearances", true);
    }

    /// <summary>Makes sure /Helv exists in the AcroForm default resources (used by the /DA strings we write).</summary>
    public static void EnsureHelvetica(PdfDocument doc, PdfDictionary acro)
    {
        var dr = acro.Elements.GetDictionary("/DR");
        if (dr is null) { dr = new PdfDictionary(doc); acro.Elements["/DR"] = dr; }
        var fonts = dr.Elements.GetDictionary("/Font");
        if (fonts is null) { fonts = new PdfDictionary(doc); dr.Elements["/Font"] = fonts; }
        if (fonts.Elements.ContainsKey("/Helv")) return;

        var helv = new PdfDictionary(doc);
        helv.Elements.SetName("/Type", "/Font");
        helv.Elements.SetName("/Subtype", "/Type1");
        helv.Elements.SetName("/BaseFont", "/Helvetica");
        helv.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(helv);
        fonts.Elements["/Helv"] = helv.Reference;
    }

    private static string? InheritedType(PdfDictionary field)
    {
        var f = field;
        while (f is not null)
        {
            if (f.Elements.GetName("/FT") is { Length: > 0 } ft) return ft;
            f = Resolve(f.Elements["/Parent"]) as PdfDictionary;
        }
        return null;
    }

    private static PdfItem EnsureIndirect(PdfDocument doc, PdfDictionary dict)
    {
        if (!dict.IsIndirect) doc.Internals.AddObject(dict);
        return dict.Reference!;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;
}
