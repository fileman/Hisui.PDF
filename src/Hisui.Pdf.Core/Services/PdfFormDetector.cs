using System.Globalization;
using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PigRect = UglyToad.PdfPig.Core.PdfRectangle;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Heuristic form-field detection over PdfPig letters and paths. Works in raw page space (y up, /Rotate
/// removed) and rotates the resulting rects back to the rendered orientation.
/// Rules: underscore runs → text fields; symbol-font box glyphs → check boxes; small white filled rectangles
/// (≥ 6 on a page, i.e. a grid) → text fields named from row label + rotated column header.
/// </summary>
internal sealed class PdfFormDetector : IPdfFormDetector
{
    private const double LineTolerance = 2.5;      // pt: letters within this baseline distance share a line
    private const double MinUnderscoreRun = 24;    // pt
    private const double MaxUnderscoreGap = 6;     // pt between "__ __" groups
    private const double MaxLabelGap = 14;         // pt between label letters
    private const int MinCellsForGrid = 6;

    public Task<IReadOnlyList<FormFieldSpec>> DetectFieldsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<FormFieldSpec>>(() =>
        {
            var (flat, rotations) = PageGeometry.Unrotate(pdf);
            var (existingRects, usedNames) = ReadExisting(flat);

            var result = new List<FormFieldSpec>();
            using var doc = UglyToad.PdfPig.PdfDocument.Open(flat);
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var pageIndex = page.Number - 1;
                var turn = pageIndex < rotations.Length ? rotations[pageIndex] : 0;
                var taken = existingRects.TryGetValue(pageIndex, out var r) ? r : [];

                var candidates = new List<Candidate>();
                var letters = page.Letters.Select((l, i) => new L(l, i)).ToList();
                DetectUnderscores(letters, candidates);
                DetectCheckBoxes(letters, candidates);
                DetectGridCells(page, letters, candidates);
                DetectRuledCells(page, letters, candidates);

                var pageSpecs = candidates
                    .Where(c => !Overlaps(c.Rect, taken))
                    .Select(c => (Candidate: c,
                                  Rect: Normalize(c.Rect, page.Width, page.Height).RotateClockwise(turn),
                                  Extent: Normalize(c.Extent, page.Width, page.Height).RotateClockwise(turn)));

                // Tab order = reading order of the page as rendered: columns first, then rows left to right.
                foreach (var (c, rect, _) in ReadingOrder.Sort(pageSpecs, x => x.Extent, x => x.Rect))
                    result.Add(new FormFieldSpec
                    {
                        PageIndex = pageIndex,
                        Rect = rect,
                        Type = c.Type,
                        Name = Unique(usedNames, c.Name),
                        Tooltip = c.Tooltip,
                    });
            }
            return result;
        }, ct);
    }

    // ── detection rules ──────────────────────────────────────────────────────

    private static void DetectUnderscores(List<L> letters, List<Candidate> output)
    {
        foreach (var line in Lines(letters.Where(l => l.Horizontal)))
        {
            var visible = line.Where(l => !l.IsSpace).ToList();
            for (var i = 0; i < visible.Count;)
            {
                if (visible[i].Value != "_") { i++; continue; }
                var start = i;
                while (i + 1 < visible.Count && visible[i + 1].Value == "_" &&
                       visible[i + 1].Left - visible[i].Right <= MaxUnderscoreGap) i++;
                var run = visible.GetRange(start, i - start + 1);
                i++;

                var left = run[0].Left;
                var right = run[^1].Right;
                if (run.Count < 3 || right - left < MinUnderscoreRun) continue;

                var fs = run[0].FontSize;
                var bottom = run.Min(l => l.Bottom) - 2;
                var rect = new PigRect(left, bottom, right, bottom + Math.Clamp(fs * 1.1, 9, 16));

                var label = LabelBefore(line, run[0], out var labelLeft);
                var extent = rect;
                if (label.Length > 0) extent = new PigRect(Math.Min(labelLeft, rect.Left), rect.Bottom, rect.Right, rect.Top);
                else label = LabelAbove(letters, run[0].BaselineY, fs, left, right);
                output.Add(new Candidate(rect, PdfFormFieldType.Text, Sanitize(label, "Campo"), label.Length > 0 ? label : null, extent));
            }
        }
    }

    private static void DetectCheckBoxes(List<L> letters, List<Candidate> output)
    {
        var lines = Lines(letters.Where(l => l.Horizontal)).ToList();
        foreach (var box in letters.Where(l => l.IsCheckGlyph))
        {
            var side = Math.Clamp(Math.Max(box.Width, box.Height) + 2, 8, 14);
            var cx = (box.Left + box.Right) / 2;
            var cy = (box.Bottom + box.Top) / 2;
            var rect = new PigRect(cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2);

            var line = lines.FirstOrDefault(ln => Math.Abs(ln[0].BaselineY - box.BaselineY) <= LineTolerance && ln.Contains(box));
            var after = line is null
                ? []
                : line.Where(l => l.Left >= box.Right - 1 && !l.IsCheckGlyph).ToList();
            var chain = TakeChain(after).ToList();
            var text = BuildText(chain);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shortName = string.Join(' ', words.Take(3));
            var extent = chain.Count > 0
                ? new PigRect(rect.Left, rect.Bottom, Math.Max(rect.Right, chain.Max(l => l.Right)), rect.Top)
                : rect;
            output.Add(new Candidate(rect, PdfFormFieldType.CheckBox, "chk_" + Sanitize(shortName, "casella"),
                text.Length > 0 ? Truncate(text, 90) : null, extent));
        }
    }

    private static void DetectGridCells(Page page, List<L> letters, List<Candidate> output)
    {
        // Painted cells: white fills are fields; other non-black fills (grey = "not fillable") only shape the grid.
        var painted = new List<(PigRect Rect, bool White)>();
        foreach (var path in page.Paths)
        {
            if (!path.IsFilled || path.FillColor is null) continue;
            var (r, g, b) = path.FillColor.ToRGBValues();
            if (r + g + b < 1.5) continue; // black grid lines, dark fills
            if (path.GetBoundingRectangle() is not { } bb) continue;
            if (bb.Width is < 8 or > 70 || bb.Height is < 5 or > 30) continue;
            painted.Add((bb, r >= 0.98 && g >= 0.98 && b >= 0.98));
        }

        // The same cell is often painted twice (outer + inset); keep the outer one.
        var kept = new List<(PigRect Rect, bool White)>();
        foreach (var c in painted.OrderByDescending(c => c.Rect.Width * c.Rect.Height))
        {
            var cx = (c.Rect.Left + c.Rect.Right) / 2;
            var cy = (c.Rect.Bottom + c.Rect.Top) / 2;
            if (!kept.Any(k => cx >= k.Rect.Left && cx <= k.Rect.Right && cy >= k.Rect.Bottom && cy <= k.Rect.Top)) kept.Add(c);
        }
        var cells = kept.Where(c => c.White).Select(c => c.Rect).ToList();
        if (cells.Count < MinCellsForGrid) return;

        // Blank (unpainted) cells are the white of the page: recover them from the row × column lattice
        // spanned by the painted cells.
        var columns = Cluster(kept.Select(c => (c.Rect.Left, c.Rect.Right)));
        var rowsLattice = Cluster(kept.Select(c => (c.Rect.Bottom, c.Rect.Top)));
        var occupied = new HashSet<(int, int)>();
        foreach (var (rect, _) in kept)
            occupied.Add((NearestIndex(columns, rect.Left), NearestIndex(rowsLattice, rect.Bottom)));
        for (var ci = 0; ci < columns.Count; ci++)
            for (var ri = 0; ri < rowsLattice.Count; ri++)
                if (!occupied.Contains((ci, ri)))
                    cells.Add(new PigRect(columns[ci].Lo, rowsLattice[ri].Lo, columns[ci].Hi, rowsLattice[ri].Hi));

        var horizontal = letters.Where(l => l.Horizontal && !l.IsSpace).ToList();
        var rotated = letters.Where(l => !l.Horizontal).ToList();
        var headers = new Dictionary<int, string>();

        // Rows by vertical centre, split into segments wherever there is a wide gap (side-by-side tables).
        var rows = new List<List<PigRect>>();
        foreach (var cell in cells.OrderByDescending(c => (c.Bottom + c.Top) / 2))
        {
            var cy = (cell.Bottom + cell.Top) / 2;
            var row = rows.FirstOrDefault(rw => Math.Abs((rw[0].Bottom + rw[0].Top) / 2 - cy) <= 2);
            if (row is null) rows.Add(row = []);
            row.Add(cell);
        }

        // Side-by-side tables ("halves"): runs of lattice columns separated by a wide gutter. A row label sits
        // between the previous half and this one, however many grey columns precede the first white cell.
        var halfOf = new int[columns.Count];
        var halfLeft = new List<double>();
        var halfRight = new List<double>();
        for (var ci = 0; ci < columns.Count; ci++)
        {
            if (ci == 0 || columns[ci].Lo - columns[ci - 1].Hi > 30)
            {
                halfLeft.Add(columns[ci].Lo);
                halfRight.Add(columns[ci].Hi);
            }
            else halfRight[^1] = columns[ci].Hi;
            halfOf[ci] = halfLeft.Count - 1;
        }

        foreach (var row in rows)
        {
            row.Sort((x, y) => x.Left.CompareTo(y.Left));
            var labels = new Dictionary<int, string>(); // half → row label
            foreach (var cell in row)
            {
                var ci = NearestIndex(columns, cell.Left);
                var half = ci >= 0 ? halfOf[ci] : 0;
                if (!labels.TryGetValue(half, out var rowLabel))
                {
                    var from = half > 0 ? halfRight[half - 1] : 0;
                    var to = half < halfLeft.Count ? halfLeft[half] + 1 : cell.Left + 1;
                    var low = cell.Bottom - 1;
                    var high = cell.Top + 1;
                    labels[half] = rowLabel = BuildText(horizontal
                        .Where(l => (l.Bottom + l.Top) / 2 >= low && (l.Bottom + l.Top) / 2 <= high &&
                                    l.Left >= from && l.Right <= to)
                        .OrderBy(l => l.Left)).Trim();
                }

                var key = (int)Math.Round(cell.Left);
                if (!headers.TryGetValue(key, out var header))
                    headers[key] = header = BuildText(rotated
                        .Where(l => (l.Left + l.Right) / 2 >= cell.Left && (l.Left + l.Right) / 2 <= cell.Right &&
                                    l.Bottom >= cell.Top - 1)
                        .OrderBy(l => l.Index)).Trim();

                var name = Sanitize(rowLabel, "riga") + "__" + Sanitize(header, "colonna");
                var tip = rowLabel.Length > 0 && header.Length > 0 ? $"{rowLabel} - {header}" : (rowLabel + header);
                var rect = new PigRect(cell.Left + 0.5, cell.Bottom + 0.5, cell.Right - 0.5, cell.Top - 0.5);
                // Order cells row by row inside their table half: grey columns between white cells are not a gutter.
                var extent = half < halfLeft.Count
                    ? new PigRect(halfLeft[half], rect.Bottom, halfRight[half], rect.Top)
                    : rect;
                output.Add(new Candidate(rect, PdfFormFieldType.Text, Truncate(name, 90), tip.Length > 0 ? tip : null, extent));
            }
        }
    }

    /// <summary>
    /// Ruled tables (cells drawn with lines): every empty cell with header text somewhere above it in its column
    /// becomes a text field, named from the row label (text to the left) and the nearest header.
    /// ponytail: cells need 4 full borders; label|value rows with no header above are skipped.
    /// </summary>
    private static void DetectRuledCells(Page page, List<L> letters, List<Candidate> output)
    {
        var hRaw = new List<(double Pos, double Lo, double Hi)>(); // y, x0, x1
        var vRaw = new List<(double Pos, double Lo, double Hi)>(); // x, y0, y1
        foreach (var path in page.Paths)
        {
            var bb = path.GetBoundingRectangle();
            if (path.IsFilled && !path.IsStroked && !(bb is { } b && (b.Width < 1.5 || b.Height < 1.5))) continue;
            foreach (var sub in path.Where(s => s.Commands.Count > 0))
            {
                var first = default(UglyToad.PdfPig.Core.PdfPoint);
                var last = first;
                foreach (var cmd in sub.Commands)
                {
                    switch (cmd)
                    {
                        case UglyToad.PdfPig.Core.PdfSubpath.Move m: first = last = m.Location; break;
                        case UglyToad.PdfPig.Core.PdfSubpath.Line ln: AddSegment(ln.From, ln.To); last = ln.To; break;
                        case UglyToad.PdfPig.Core.PdfSubpath.Close: AddSegment(last, first); last = first; break;
                    }
                }
            }
        }

        void AddSegment(UglyToad.PdfPig.Core.PdfPoint a, UglyToad.PdfPig.Core.PdfPoint b)
        {
            if (Math.Abs(a.Y - b.Y) < 0.6 && Math.Abs(a.X - b.X) >= 4) hRaw.Add(((a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
            else if (Math.Abs(a.X - b.X) < 0.6 && Math.Abs(a.Y - b.Y) >= 4) vRaw.Add(((a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        }

        var hs = MergeLines(hRaw);
        var vs = MergeLines(vRaw);
        if (hs.Count < 2 || vs.Count < 2) return;

        // Minimal rectangles: top edge h, left edge v1, nearest right edge v2, nearest bottom edge spanning both.
        const double T = 1.5;
        var cells = new List<PigRect>();
        foreach (var h in hs)
        {
            var down = vs.Where(v => v.Pos >= h.Lo - T && v.Pos <= h.Hi + T && v.Hi >= h.Pos - T && v.Lo < h.Pos - 4)
                .OrderBy(v => v.Pos).ToList();
            for (var i = 0; i + 1 < down.Count; i++)
            {
                var v1 = down[i];
                var v2 = down[i + 1];
                if (v2.Pos - v1.Pos < 8) continue;
                var bottom = hs.Where(b => b.Pos < h.Pos - 4 && b.Lo <= v1.Pos + T && b.Hi >= v2.Pos - T &&
                                           v1.Lo <= b.Pos + T && v2.Lo <= b.Pos + T)
                    .OrderByDescending(b => b.Pos).FirstOrDefault();
                if (bottom == default) continue;
                var cell = new PigRect(v1.Pos, bottom.Pos, v2.Pos, h.Pos);
                if (cell.Height >= 7 && cell.Width >= 10 && !cells.Any(c => Near(c, cell))) cells.Add(cell);
            }
        }

        var text = letters.Where(l => !l.IsSpace && l.Value != "_").ToList();
        string TextIn(PigRect c) => BuildText(text.Where(l => l.Horizontal && Center(l) is var (x, y) &&
            x >= c.Left - 1 && x <= c.Right + 1 && y >= c.Bottom - 1 && y <= c.Top + 1).OrderByDescending(l => (int)(l.BaselineY / LineTolerance)).ThenBy(l => l.Left));
        var content = cells.Select(c => (Cell: c, Text: TextIn(c))).ToList();

        foreach (var (cell, t) in content.Where(c => c.Text.Length == 0))
        {
            // Header: texts of the cells stacked above, nearest first. Row label: nearest text cell to the left.
            var headers = new List<string>();
            var cur = cell;
            for (; ; )
            {
                var above = content.Where(c => Math.Abs(c.Cell.Bottom - cur.Top) <= T &&
                        Math.Min(c.Cell.Right, cur.Right) - Math.Max(c.Cell.Left, cur.Left) > 0.5 * cur.Width)
                    .Select(c => ((PigRect Cell, string Text)?)c).FirstOrDefault();
                if (above is null) break;
                cur = above.Value.Cell;
                if (above.Value.Text.Length > 0) headers.Add(above.Value.Text);
            }
            if (headers.Count == 0) continue;

            var rowLabel = content.Where(c => c.Text.Length > 0 && c.Cell.Right <= cell.Left + T &&
                    Math.Min(c.Cell.Top, cell.Top) - Math.Max(c.Cell.Bottom, cell.Bottom) > 0.5 * cell.Height)
                .OrderByDescending(c => c.Cell.Right).Select(c => c.Text).FirstOrDefault() ?? "";

            var name = string.Join("__", new[] { rowLabel, headers[0] }.Where(s => s.Length > 0).Select(s => Sanitize(s, "campo")));
            var tip = string.Join(" - ", new[] { rowLabel }.Concat(Enumerable.Reverse(headers)).Where(s => s.Length > 0));
            var rect = new PigRect(cell.Left + 0.5, cell.Bottom + 0.5, cell.Right - 0.5, cell.Top - 0.5);
            if (output.Any(o => Overlaps(rect, [o.Rect]))) continue;
            output.Add(new Candidate(rect, PdfFormFieldType.Text, Truncate(name, 90), Truncate(tip, 160), rect));
        }

        static bool Near(PigRect a, PigRect b) =>
            Math.Abs(a.Left - b.Left) < 1 && Math.Abs(a.Right - b.Right) < 1 && Math.Abs(a.Top - b.Top) < 1 && Math.Abs(a.Bottom - b.Bottom) < 1;
        static (double, double) Center(L l) => ((l.Left + l.Right) / 2, (l.Bottom + l.Top) / 2);
    }

    /// <summary>Merges collinear (±1 pt) segments that touch or overlap into single lines.</summary>
    private static List<(double Pos, double Lo, double Hi)> MergeLines(List<(double Pos, double Lo, double Hi)> raw)
    {
        var merged = new List<(double Pos, double Lo, double Hi)>();
        foreach (var s in raw.OrderBy(s => s.Pos).ThenBy(s => s.Lo))
        {
            var i = merged.FindIndex(m => Math.Abs(m.Pos - s.Pos) <= 1 && s.Lo <= m.Hi + 1.5 && s.Hi >= m.Lo - 1.5);
            if (i < 0) merged.Add(s);
            else merged[i] = (merged[i].Pos, Math.Min(merged[i].Lo, s.Lo), Math.Max(merged[i].Hi, s.Hi));
        }
        return merged;
    }

    /// <summary>Groups (lo, hi) spans whose lo values agree within 1.5 pt; keeps clusters with at least 3 members.</summary>
    private static List<(double Lo, double Hi)> Cluster(IEnumerable<(double Lo, double Hi)> spans)
    {
        var groups = new List<List<(double Lo, double Hi)>>();
        foreach (var span in spans.OrderBy(s => s.Lo))
        {
            if (groups.Count > 0 && span.Lo - groups[^1][0].Lo <= 1.5) groups[^1].Add(span);
            else groups.Add([span]);
        }
        return groups.Where(g => g.Count >= 3)
            .Select(g => (g.Average(x => x.Lo), g.Average(x => x.Hi))).ToList();
    }

    private static int NearestIndex(List<(double Lo, double Hi)> clusters, double lo)
    {
        var best = -1;
        var bestDist = 2.0;
        for (var i = 0; i < clusters.Count; i++)
        {
            var d = Math.Abs(clusters[i].Lo - lo);
            if (d <= bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    // ── labels ───────────────────────────────────────────────────────────────

    /// <summary>The run of letters directly left of <paramref name="run"/> on its line, stopping at underscores or wide gaps.</summary>
    private static string LabelBefore(List<L> line, L first, out double labelLeft)
    {
        var before = line.Where(l => l.Right <= first.Left + 0.5).OrderByDescending(l => l.Left).ToList();
        var chain = new List<L>();
        L? prev = null;
        foreach (var l in before)
        {
            if (l.Value == "_" || l.IsCheckGlyph) break;
            if (prev is not null && prev.Left - l.Right > MaxLabelGap) break;
            chain.Add(l);
            if (!l.IsSpace) prev = l;
        }
        chain.Reverse();
        labelLeft = chain.Count > 0 ? chain.Min(l => l.Left) : first.Left;
        return CleanLabel(BuildText(chain));
    }

    /// <summary>Text of the nearest line above, restricted to the horizontal span of the blank.</summary>
    private static string LabelAbove(List<L> letters, double baselineY, double fs, double left, double right)
    {
        var candidates = Lines(letters.Where(l => l.Horizontal && !l.IsSpace && l.Value != "_" && !l.IsCheckGlyph))
            .Where(ln => ln[0].BaselineY > baselineY + 2 && ln[0].BaselineY <= baselineY + fs * 4.5)
            .OrderBy(ln => ln[0].BaselineY);
        foreach (var line in candidates)
        {
            var text = CleanLabel(BuildText(line.Where(l => (l.Left + l.Right) / 2 >= left - 5 && (l.Left + l.Right) / 2 <= right + 5)));
            if (text.Length > 0) return text;
        }
        return "";
    }

    private static string CleanLabel(string text) => text.Trim().TrimEnd(':', '.', ' ', '-').Trim();

    // ── text helpers ─────────────────────────────────────────────────────────

    /// <summary>First letters of <paramref name="letters"/> (ordered by x) while the gap stays small.</summary>
    private static IEnumerable<L> TakeChain(List<L> letters)
    {
        L? prev = null;
        foreach (var l in letters.OrderBy(l => l.Left))
        {
            if (prev is not null && !l.IsSpace && l.Left - prev.Right > MaxLabelGap) yield break;
            yield return l;
            if (!l.IsSpace) prev = l;
        }
    }

    /// <summary>Joins letters in the given order, adding a space where the text visibly gaps or a space glyph sits.</summary>
    private static string BuildText(IEnumerable<L> letters)
    {
        var sb = new StringBuilder();
        L? prev = null;
        foreach (var l in letters)
        {
            if (l.IsSpace) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (prev is not null && l.AdvanceStart - prev.AdvanceEnd > 0.25 * l.FontSize && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(l.Value);
            prev = l;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Clusters horizontal letters into lines by baseline (top line first), each sorted left to right.</summary>
    private static IEnumerable<List<L>> Lines(IEnumerable<L> letters)
    {
        var lines = new List<List<L>>();
        foreach (var l in letters.OrderByDescending(l => l.BaselineY))
        {
            var line = lines.FirstOrDefault(ln => Math.Abs(ln[0].BaselineY - l.BaselineY) <= LineTolerance);
            if (line is null) lines.Add(line = []);
            line.Add(l);
        }
        foreach (var ln in lines) ln.Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }

    private static string Sanitize(string text, string fallback)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? fallback : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd('_', ' ');

    private static string Unique(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    // ── geometry / existing fields ───────────────────────────────────────────

    private static PdfRect Normalize(PigRect r, double w, double h) =>
        new(r.Left / w, (h - r.Top) / h, r.Right / w, (h - r.Bottom) / h);

    private static bool Overlaps(PigRect c, List<PigRect> existing)
    {
        var area = c.Width * c.Height;
        if (area <= 0) return false;
        foreach (var e in existing)
        {
            var w = Math.Min(c.Right, e.Right) - Math.Max(c.Left, e.Left);
            var h = Math.Min(c.Top, e.Top) - Math.Max(c.Bottom, e.Bottom);
            if (w > 0 && h > 0 && w * h > 0.5 * area) return true;
        }
        return false;
    }

    /// <summary>Widget rects per page (raw space, y up) and every field name already in the document.</summary>
    private static (Dictionary<int, List<PigRect>> Rects, HashSet<string> Names) ReadExisting(byte[] pdf)
    {
        var rects = new Dictionary<int, List<PigRect>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            for (var p = 0; p < doc.PageCount; p++)
            {
                var annots = doc.Pages[p].Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (var i = 0; i < annots.Elements.Count; i++)
                {
                    if ((annots.Elements[i] is PdfReference { Value: PdfDictionary d } ? d : annots.Elements[i] as PdfDictionary)
                        is not { } widget || widget.Elements.GetName("/Subtype") != "/Widget") continue;

                    var rc = widget.Elements.GetRectangle("/Rect");
                    if (!rects.TryGetValue(p, out var list)) rects[p] = list = [];
                    list.Add(new PigRect(Math.Min(rc.X1, rc.X2), Math.Min(rc.Y1, rc.Y2), Math.Max(rc.X1, rc.X2), Math.Max(rc.Y1, rc.Y2)));

                    for (PdfDictionary? f = widget; f is not null;
                         f = f.Elements["/Parent"] is PdfReference { Value: PdfDictionary parent } ? parent : null)
                        if (f.Elements.GetString("/T") is { Length: > 0 } t) names.Add(t);
                }
            }
        }
        catch (Exception)
        {
            // Unreadable structure: detect without de-duplication.
        }
        return (rects, names);
    }

    // ── small value types ────────────────────────────────────────────────────

    /// <param name="Extent">Field plus its label, used only to order fields (a label crossing a gutter means "same row", not "column").</param>
    private sealed record Candidate(PigRect Rect, PdfFormFieldType Type, string Name, string? Tooltip, PigRect Extent);

    /// <summary>A PdfPig letter with the derived values the rules need.</summary>
    private sealed class L
    {
        private readonly Letter _l;
        public L(Letter letter, int index) { _l = letter; Index = index; }

        public int Index { get; }
        public string Value => _l.Value;
        public bool IsSpace => string.IsNullOrWhiteSpace(_l.Value);
        public double Left => _l.GlyphRectangle.Left;
        public double Right => _l.GlyphRectangle.Right;
        public double Bottom => _l.GlyphRectangle.Bottom;
        public double Top => _l.GlyphRectangle.Top;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public double FontSize => _l.FontSize > 0 ? _l.FontSize : 10;
        public double BaselineY => _l.StartBaseLine.Y;
        public bool Horizontal => _l.TextOrientation == TextOrientation.Horizontal;

        /// <summary>Extent along the reading direction, so gaps are measured correctly for rotated text.</summary>
        public double AdvanceStart => _l.TextOrientation switch
        {
            TextOrientation.Rotate90 => Bottom, TextOrientation.Rotate270 => -Top,
            TextOrientation.Rotate180 => -Right, _ => Left,
        };
        public double AdvanceEnd => _l.TextOrientation switch
        {
            TextOrientation.Rotate90 => Top, TextOrientation.Rotate270 => -Bottom,
            TextOrientation.Rotate180 => -Left, _ => Right,
        };

        /// <summary>A small square symbol used as an empty tick box (☐ or a Symbol/Wingdings private-use glyph).</summary>
        public bool IsCheckGlyph
        {
            get
            {
                if (_l.Value.Length != 1 || Width < 4 || Width > 20 || Height < 4 || Height > 20) return false;
                var aspect = Width / Height;
                if (aspect < 0.6 || aspect > 1.6) return false;
                var c = _l.Value[0];
                if (c is '☐' or '□' or '▢' or '◻' or '❏' or '❒') return true;
                return c is >= '' and <= '' &&
                       _l.FontName is { } font &&
                       (font.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
                        font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) ||
                        font.Contains("Webdings", StringComparison.OrdinalIgnoreCase) ||
                        font.Contains("Dingbats", StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
