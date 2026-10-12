using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Hisui.Pdf.Core.Services;

/// <summary>Raw outline (bookmark) and destination handling, shared by the navigation service and the session build.</summary>
internal static class OutlineIO
{
    public static List<PdfBookmark> Read(PdfDocument doc)
    {
        var pages = PageIndexMap(doc);
        var root = doc.Internals.Catalog.Elements.GetDictionary("/Outlines");
        return root is null ? [] : ReadSiblings(doc, Resolve(root.Elements["/First"]) as PdfDictionary, pages, 0);
    }

    private static List<PdfBookmark> ReadSiblings(PdfDocument doc, PdfDictionary? item, Dictionary<PdfDictionary, int> pages, int depth)
    {
        var result = new List<PdfBookmark>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (; item is not null && depth < 32 && seen.Add(item); item = Resolve(item.Elements["/Next"]) as PdfDictionary)
        {
            var dest = item.Elements["/Dest"] ?? GoToDest(Resolve(item.Elements["/A"]) as PdfDictionary);
            result.Add(new PdfBookmark(item.Elements.GetString("/Title"), DestPage(doc, dest, pages),
                ReadSiblings(doc, Resolve(item.Elements["/First"]) as PdfDictionary, pages, depth + 1)));
        }
        return result;
    }

    /// <summary>Replaces the outline. <paramref name="pageOf"/> maps a bookmark's page index to the page to target (null: no destination).</summary>
    public static void Write(PdfDocument doc, IReadOnlyList<PdfBookmark> bookmarks, Func<int, PdfPage?> pageOf)
    {
        doc.Internals.Catalog.Elements.Remove("/Outlines");
        if (bookmarks.Count == 0) return;
        var root = new PdfDictionary(doc);
        doc.Internals.AddObject(root);
        root.Elements.SetName("/Type", "/Outlines");
        var count = WriteChildren(doc, root, bookmarks, pageOf);
        root.Elements.SetInteger("/Count", count);
        doc.Internals.Catalog.Elements["/Outlines"] = root.Reference;
    }

    private static int WriteChildren(PdfDocument doc, PdfDictionary parent, IReadOnlyList<PdfBookmark> items, Func<int, PdfPage?> pageOf)
    {
        PdfDictionary? prev = null;
        var visible = 0;
        foreach (var b in items)
        {
            var item = new PdfDictionary(doc);
            doc.Internals.AddObject(item);
            item.Elements.SetString("/Title", b.Title);
            item.Elements["/Parent"] = parent.Reference;
            if (pageOf(b.PageIndex) is { Reference: { } pageRef })
                item.Elements["/Dest"] = new PdfArray(doc, pageRef, new PdfName("/Fit"));
            if (prev is null) parent.Elements["/First"] = item.Reference;
            else { prev.Elements["/Next"] = item.Reference; item.Elements["/Prev"] = prev.Reference; }
            parent.Elements["/Last"] = item.Reference;
            visible++;
            if (b.Children.Count > 0) visible += WriteChildren(doc, item, b.Children, pageOf); // open: children counted
            if (b.Children.Count > 0) item.Elements.SetInteger("/Count", CountAll(b.Children));
            prev = item;
        }
        return visible;

        static int CountAll(IReadOnlyList<PdfBookmark> list) => list.Sum(x => 1 + CountAll(x.Children));
    }

    public static Dictionary<PdfDictionary, int> PageIndexMap(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < doc.PageCount; i++) map[doc.Pages[i]] = i;
        return map;
    }

    public static PdfItem? GoToDest(PdfDictionary? action) =>
        action is not null && action.Elements.GetName("/S") == "/GoTo" ? action.Elements["/D"] : null;

    /// <summary>Zero-based page an explicit or named destination points to; -1 when unknown.</summary>
    public static int DestPage(PdfDocument doc, PdfItem? dest, Dictionary<PdfDictionary, int> pages)
    {
        var d = Resolve(dest);
        if (d is PdfName or PdfString) d = Resolve(Named(doc, d is PdfName n ? n.Value[1..] : ((PdfString)d).Value));
        if (d is PdfDictionary dd) d = Resolve(dd.Elements["/D"]); // named destination stored as << /D [...] >>
        if (d is PdfArray { Elements.Count: > 0 } arr)
        {
            var first = Resolve(arr.Elements[0]);
            if (first is PdfDictionary page && pages.TryGetValue(page, out var index)) return index;
            if (first is PdfInteger number && number.Value >= 0 && number.Value < doc.PageCount) return number.Value; // remote-style index
        }
        return -1;
    }

    private static PdfItem? Named(PdfDocument doc, string name)
    {
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.GetDictionary("/Dests") is { } dests && dests.Elements.ContainsKey("/" + name)) return dests.Elements["/" + name];
        return catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } tree ? FindInTree(tree, name, 0) : null;
    }

    private static PdfItem? FindInTree(PdfDictionary node, string name, int depth)
    {
        if (depth > 32) return null;
        if (node.Elements.GetArray("/Names") is { } names)
            for (var i = 0; i + 1 < names.Elements.Count; i += 2)
                if ((Resolve(names.Elements[i]) as PdfString)?.Value == name) return names.Elements[i + 1];
        if (node.Elements.GetArray("/Kids") is { } kids)
            foreach (var kid in kids.Elements.Select(Resolve).OfType<PdfDictionary>())
                if (FindInTree(kid, name, depth + 1) is { } hit) return hit;
        return null;
    }

    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;
}
