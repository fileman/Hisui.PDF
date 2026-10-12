using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Structural page operations built on PDFsharp page import. Each operation opens its inputs in
/// <see cref="PdfDocumentOpenMode.Import"/>, copies the wanted pages into a fresh document and saves
/// it to a byte buffer. Work runs on the thread pool.
/// </summary>
internal sealed class PdfPageService : IPdfPageService
{
    public Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(
            () =>
            {
                using var doc = OpenImport(pdf);
                return doc.PageCount;
            },
            cancellationToken);
    }

    public Task<byte[]> MergeAsync(IReadOnlyList<byte[]> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return Task.Run(
            () =>
            {
                using var output = new PdfDocument();
                foreach (var bytes in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = OpenImport(bytes);
                    if (output.PageCount == 0) output.Info.CopyFrom(input.Info);
                    for (var i = 0; i < input.PageCount; i++)
                        AddPageSafe(output, input.Pages[i]);
                }

                return Save(output);
            },
            cancellationToken);
    }

    public Task<byte[]> ExtractPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pageIndices);
        return Task.Run(
            () =>
            {
                using var input = OpenImport(pdf);
                using var output = new PdfDocument();
                output.Info.CopyFrom(input.Info);
                foreach (var index in pageIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index < 0 || index >= input.PageCount)
                        throw new ArgumentOutOfRangeException(
                            nameof(pageIndices), $"Page index {index} is out of range (0..{input.PageCount - 1}).");
                    AddPageSafe(output, input.Pages[index]);
                }

                return Save(output);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<byte[]>> SplitEveryAsync(byte[] pdf, int pagesPerChunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerChunk);
        return Task.Run<IReadOnlyList<byte[]>>(
            () =>
            {
                using var input = OpenImport(pdf);
                var chunks = new List<byte[]>();
                for (var start = 0; start < input.PageCount; start += pagesPerChunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var output = new PdfDocument();
                    output.Info.CopyFrom(input.Info);
                    var end = Math.Min(start + pagesPerChunk, input.PageCount);
                    for (var i = start; i < end; i++)
                        AddPageSafe(output, input.Pages[i]);
                    chunks.Add(Save(output));
                }

                return chunks;
            },
            cancellationToken);
    }

    public Task<byte[]> BuildFromSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.Run(
            () =>
            {
                // One source, every page exactly once: edit it in place. Keeps everything page import loses —
                // tags (structure tree), XMP metadata, output intents, outline, links, JavaScript, attachments.
                if (TryBuildInPlace(session) is { } inPlace) return inPlace;

                // Open each source document once and reuse it across all of its page refs.
                var importers = new Dictionary<int, PdfDocument>();
                try
                {
                    using var output = new PdfDocument();
                    var placed = new Dictionary<(int Source, int Page), PdfPage>(); // first output page of each source page
                    var pendingLinks = new List<(PdfPage Page, int AnnotIndex, int Source, int TargetPage)>();
                    foreach (var pageRef in session.Pages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!importers.TryGetValue(pageRef.SourceDocumentId, out var source))
                        {
                            source = OpenImport(session.GetSource(pageRef.SourceDocumentId));
                            importers[pageRef.SourceDocumentId] = source;
                        }

                        if (output.PageCount == 0) output.Info.CopyFrom(source.Info); // metadata of the first page's document
                        // Internal links point at page objects of the source: detach them before the import (which would
                        // otherwise drag the target pages along) and re-point them at the output pages afterwards.
                        var links = DetachInternalLinks(source, source.Pages[pageRef.SourcePageIndex]);
                        var added = AddPageSafe(output, source.Pages[pageRef.SourcePageIndex]);
                        if (pageRef.Rotation != PageRotation.None)
                            added.Rotate = (added.Rotate + (int)pageRef.Rotation) % 360;
                        placed.TryAdd((pageRef.SourceDocumentId, pageRef.SourcePageIndex), added);
                        foreach (var (annot, target) in links) pendingLinks.Add((added, annot, pageRef.SourceDocumentId, target));
                    }

                    foreach (var (page, annot, sourceId, target) in pendingLinks)
                        if (page.Elements.GetArray("/Annots") is { } annots && annot < annots.Elements.Count &&
                            OutlineIO.Resolve(annots.Elements[annot]) is PdfDictionary link)
                        {
                            // A target page left out of the output leaves the link inert.
                            if (placed.TryGetValue((sourceId, target), out var to))
                                link.Elements["/Dest"] = new PdfArray(output, to.Reference!, new PdfName("/Fit"));
                        }

                    // Outline: each source's bookmarks, in order of first appearance, re-targeted at the output pages.
                    var outline = new List<PdfBookmark>();
                    foreach (var id in session.Pages.Select(p => p.SourceDocumentId).Distinct())
                        outline.AddRange(Retarget(OutlineIO.Read(importers[id]), id));
                    var flatPages = outline.Count == 0 ? [] : placed.Values.ToList();
                    if (outline.Count > 0)
                        OutlineIO.Write(output, outline, i => i >= 0 && i < flatPages.Count ? flatPages[i] : null);

                    return Save(output);

                    // Bookmark page indices become indices into placed.Values (insertion order = output order).
                    IEnumerable<PdfBookmark> Retarget(IEnumerable<PdfBookmark> items, int sourceId) => items.Select(b =>
                    {
                        var index = -1;
                        if (b.PageIndex >= 0 && placed.TryGetValue((sourceId, b.PageIndex), out var target))
                            index = placed.Values.ToList().IndexOf(target);
                        return new PdfBookmark(b.Title, index, Retarget(b.Children, sourceId).ToList());
                    });
                }
                finally
                {
                    foreach (var importer in importers.Values)
                        importer.Dispose();
                }
            },
            cancellationToken);
    }

    /// <summary>The fast path: a single source whose pages each appear once (reordered/rotated at most).</summary>
    private static byte[]? TryBuildInPlace(PdfDocumentSession session)
    {
        if (session.Pages.Count == 0 || session.Pages.Select(p => p.SourceDocumentId).Distinct().Count() != 1) return null;
        var source = session.GetSource(session.Pages[0].SourceDocumentId);
        var order = session.Pages.Select(p => p.SourcePageIndex).ToList();
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            if (order.Count != doc.PageCount || order.Distinct().Count() != order.Count) return null;
            var current = Enumerable.Range(0, doc.PageCount).ToList();
            for (var target = 0; target < order.Count; target++)
            {
                var at = current.IndexOf(order[target]);
                if (at == target) continue;
                doc.Pages.MovePage(at, target);
                current.RemoveAt(at);
                current.Insert(target, order[target]);
            }
            for (var i = 0; i < session.Pages.Count; i++)
                if (session.Pages[i].Rotation != PageRotation.None)
                    doc.Pages[i].Rotate = (doc.Pages[i].Rotate + (int)session.Pages[i].Rotation) % 360;

            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return output.ToArray();
        }
        catch (Exception)
        {
            return null; // unusual structure: fall back to the page-import path
        }
    }

    /// <summary>Removes the internal (GoTo) destinations of <paramref name="page"/>'s links; returns (annot index, target page).</summary>
    private static List<(int Annot, int Target)> DetachInternalLinks(PdfDocument source, PdfPage page)
    {
        var result = new List<(int, int)>();
        if (page.Elements.GetArray("/Annots") is not { } annots) return result;
        Dictionary<PdfDictionary, int>? pages = null;
        for (var i = 0; i < annots.Elements.Count; i++)
        {
            if (OutlineIO.Resolve(annots.Elements[i]) is not PdfDictionary a || a.Elements.GetName("/Subtype") != "/Link") continue;
            var action = OutlineIO.Resolve(a.Elements["/A"]) as PdfDictionary;
            var dest = a.Elements["/Dest"] ?? OutlineIO.GoToDest(action);
            if (dest is null) continue;
            pages ??= OutlineIO.PageIndexMap(source);
            result.Add((i, OutlineIO.DestPage(source, dest, pages)));
            a.Elements.Remove("/Dest");
            if (OutlineIO.GoToDest(action) is not null) a.Elements.Remove("/A");
        }
        return result;
    }

    // PDFsharp's closure walk throws NullReferenceException when a page key (e.g. /Rotate) is an
    // indirect non-container object. Inline those values on the page before importing it.
    private static PdfPage AddPageSafe(PdfDocument output, PdfPage page)
    {
        foreach (var key in page.Elements.Keys.ToList())
        {
            if (page.Elements[key] is not PdfReference { Value: { } target } || target is PdfDictionary or PdfArray)
                continue;
            switch (target)
            {
                case PdfIntegerObject i: page.Elements[key] = new PdfInteger(i.Value); break;
                case PdfRealObject r: page.Elements[key] = new PdfReal(r.Value); break;
                case PdfBooleanObject b: page.Elements[key] = new PdfBoolean(b.Value); break;
                default: page.Elements.Remove(key); break;
            }
        }

        return output.AddPage(page);
    }

    private static PdfDocument OpenImport(byte[] bytes)
    {
        // A fresh, read-only buffer per open — PdfPig and PDFsharp must never share a stream handle.
        var stream = new MemoryStream(bytes, writable: false);
        return PdfReader.Open(stream, PdfDocumentOpenMode.Import);
    }

    private static byte[] Save(PdfDocument document)
    {
        AcroFormRestorer.Restore(document); // page import drops /AcroForm; keep imported widgets fillable
        using var stream = new MemoryStream();
        document.SaveStamped(stream);
        return stream.ToArray();
    }
}
