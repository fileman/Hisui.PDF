using System.Globalization;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>Markup annotations read/written as real /Annots entries (PDFium draws a default look when /AP is missing).</summary>
internal sealed class PdfCommentService : IPdfCommentService
{
    private static readonly HashSet<string> Markup =
    [
        "/Text", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline",
        "/Squiggly", "/StrikeOut", "/Stamp", "/Caret", "/Ink", "/FileAttachment", "/Sound", "/Redact",
    ];

    private const double NoteSize = 20; // pt, the sticky-note icon

    public Task<IReadOnlyList<PdfComment>> ReadCommentsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<PdfComment>>(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var result = new List<PdfComment>();
            for (var p = 0; p < doc.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[p];
                if (page.Elements.GetArray("/Annots") is not { } annots) continue;
                var items = annots.Elements.Select(Resolve).ToList();
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not PdfDictionary a || !Markup.Contains(a.Elements.GetName("/Subtype"))) continue;
                    int? irt = Resolve(a.Elements["/IRT"]) is PdfDictionary parent && items.IndexOf(parent) is >= 0 and var at ? at : null;
                    result.Add(new PdfComment
                    {
                        PageIndex = p,
                        AnnotIndex = i,
                        Subtype = a.Elements.GetName("/Subtype")[1..],
                        Rect = PageGeometry.ToVisual(page, a.Elements.GetRectangle("/Rect")),
                        Text = a.Elements.GetString("/Contents"),
                        Author = a.Elements.GetString("/T") is { Length: > 0 } t ? t : null,
                        Modified = ParseDate(a.Elements.GetString("/M")) ?? ParseDate(a.Elements.GetString("/CreationDate")),
                        InReplyTo = irt,
                        IsHidden = (a.Elements.GetInteger("/F") & 2) != 0,
                    });
                }
            }
            return result;
        }, ct);
    }

    public Task<byte[]> AddCommentAsync(byte[] pdf, int pageIndex, PdfRect at, string text, string author, CancellationToken ct = default) =>
        Edit(pdf, pageIndex, ct, (doc, page, annots) =>
        {
            var (vw, vh) = PageGeometry.VisualSize(page);
            var left = Math.Clamp(at.Left, 0, 1 - NoteSize / vw);
            var top = Math.Clamp(at.Top, 0, 1 - NoteSize / vh);
            var note = NewNote(doc, page, PageGeometry.ToRaw(page, PdfRect.FromLTWH(left, top, NoteSize / vw, NoteSize / vh)), text, author);
            annots.Elements.Add(note.Reference!);
        });

    public Task<byte[]> ReplyAsync(byte[] pdf, int pageIndex, int annotIndex, string text, string author, CancellationToken ct = default) =>
        Edit(pdf, pageIndex, ct, (doc, page, annots) =>
        {
            var parent = At(annots, annotIndex);
            var reply = NewNote(doc, page, parent.Elements.GetRectangle("/Rect"), text, author);
            reply.Elements["/IRT"] = parent.Reference ?? throw new InvalidOperationException("The comment is not an indirect object.");
            reply.Elements.SetName("/RT", "/R");
            annots.Elements.Add(reply.Reference!);
        });

    public Task<byte[]> SetTextAsync(byte[] pdf, int pageIndex, int annotIndex, string text, CancellationToken ct = default) =>
        Edit(pdf, pageIndex, ct, (_, _, annots) =>
        {
            var a = At(annots, annotIndex);
            a.Elements.SetString("/Contents", text);
            a.Elements.SetString("/M", PdfDate(DateTime.Now));
            // A FreeText shows its text through /AP; drop it so the viewer redraws the new text.
            if (a.Elements.GetName("/Subtype") == "/FreeText") a.Elements.Remove("/AP");
        });

    public Task<byte[]> MoveAsync(byte[] pdf, int pageIndex, int annotIndex, double dx, double dy, CancellationToken ct = default) =>
        Edit(pdf, pageIndex, ct, (_, page, annots) =>
        {
            var a = At(annots, annotIndex);
            var oldRaw = a.Elements.GetRectangle("/Rect");
            var visual = PageGeometry.ToVisual(page, oldRaw);
            var newRaw = PageGeometry.ToRaw(page, new PdfRect(visual.Left + dx, visual.Top + dy, visual.Right + dx, visual.Bottom + dy));
            var (ox, oy) = (newRaw.X1 - oldRaw.X1, newRaw.Y1 - oldRaw.Y1);
            Shift(a, ox, oy);
            if (Resolve(a.Elements["/Popup"]) is PdfDictionary popup) Shift(popup, ox, oy);
            a.Elements.SetString("/M", PdfDate(DateTime.Now));
        });

    public Task<byte[]> DeleteAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default) =>
        Edit(pdf, pageIndex, ct, (_, _, annots) =>
        {
            var doomed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { At(annots, annotIndex) };
            // Popups and the whole reply thread go with it.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var d in annots.Elements.Select(Resolve).OfType<PdfDictionary>().ToList())
                {
                    if (doomed.Contains(d)) continue;
                    if ((Resolve(d.Elements["/IRT"]) is PdfDictionary irt && doomed.Contains(irt)) ||
                        (Resolve(d.Elements["/Parent"]) is PdfDictionary owner && doomed.Contains(owner) && d.Elements.GetName("/Subtype") == "/Popup"))
                        grew |= doomed.Add(d);
                }
            }
            for (var i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is PdfDictionary d && doomed.Contains(d)) annots.Elements.RemoveAt(i);
        });

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Task<byte[]> Edit(byte[] pdf, int pageIndex, CancellationToken ct, Action<PdfDocument, PdfPage, PdfArray> edit)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            if ((uint)pageIndex >= (uint)doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var page = doc.Pages[pageIndex];
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            edit(doc, page, annots);
            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return output.ToArray();
        }, ct);
    }

    private static PdfDictionary At(PdfArray annots, int index) =>
        (uint)index < (uint)annots.Elements.Count && Resolve(annots.Elements[index]) is PdfDictionary a && Markup.Contains(a.Elements.GetName("/Subtype"))
            ? a
            : throw new ArgumentOutOfRangeException(nameof(index), "No comment at that index.");

    private static PdfDictionary NewNote(PdfDocument doc, PdfPage page, PdfRectangle rect, string text, string author)
    {
        var note = new PdfDictionary(doc);
        doc.Internals.AddObject(note);
        note.Elements.SetName("/Type", "/Annot");
        note.Elements.SetName("/Subtype", "/Text");
        note.Elements.SetRectangle("/Rect", rect);
        note.Elements.SetString("/Contents", text);
        note.Elements.SetString("/T", author);
        var now = PdfDate(DateTime.Now);
        note.Elements.SetString("/M", now);
        note.Elements.SetString("/CreationDate", now);
        note.Elements.SetString("/NM", Guid.NewGuid().ToString());
        note.Elements.SetName("/Name", "/Comment");
        note.Elements.SetInteger("/F", 28); // Print | NoZoom | NoRotate, the usual flags of a note
        note.Elements["/C"] = new PdfArray(doc, new PdfReal(1), new PdfReal(0.82), new PdfReal(0));
        note.Elements["/P"] = page.Reference;
        return note;
    }

    /// <summary>Moves every coordinate an annotation carries.</summary>
    private static void Shift(PdfDictionary a, double dx, double dy)
    {
        var r = a.Elements.GetRectangle("/Rect");
        a.Elements.SetRectangle("/Rect", new PdfRectangle(new PdfSharp.Drawing.XPoint(r.X1 + dx, r.Y1 + dy), new PdfSharp.Drawing.XPoint(r.X2 + dx, r.Y2 + dy)));
        foreach (var key in new[] { "/QuadPoints", "/Vertices", "/L", "/CL" })
            if (Resolve(a.Elements[key]) is PdfArray pts) ShiftPoints(pts, dx, dy);
        if (Resolve(a.Elements["/InkList"]) is PdfArray ink)
            foreach (var stroke in ink.Elements.Select(Resolve).OfType<PdfArray>()) ShiftPoints(stroke, dx, dy);
    }

    private static void ShiftPoints(PdfArray pts, double dx, double dy)
    {
        for (var i = 0; i < pts.Elements.Count; i++)
        {
            var v = Resolve(pts.Elements[i]) switch { PdfReal r => r.Value, PdfInteger n => n.Value, _ => double.NaN };
            if (!double.IsNaN(v)) pts.Elements[i] = new PdfReal(v + (i % 2 == 0 ? dx : dy));
        }
    }

    private static string PdfDate(DateTime t)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(t);
        return $"D:{t:yyyyMMddHHmmss}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }

    internal static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var digits = new string(s.StartsWith("D:") ? s[2..].TakeWhile(char.IsDigit).ToArray() : s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        digits = digits.PadRight(14, '0')[..14];
        return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;
}
