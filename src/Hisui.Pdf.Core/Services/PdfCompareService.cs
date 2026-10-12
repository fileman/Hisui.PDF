using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Word-level text comparison, page by page (page i of A with page i of B), with an LCS diff per page.
/// ponytail: pages are paired by position, so an inserted page shifts every later pair; layout/image changes are not detected.
/// </summary>
internal sealed class PdfCompareService : IPdfCompareService
{
    private const long MaxCells = 16_000_000; // LCS table cap per page pair (~4000 x 4000 words)
    private readonly IPdfTextExtractor _text;

    public PdfCompareService(IPdfTextExtractor text) => _text = text;

    public async Task<DocumentComparison> CompareAsync(byte[] original, byte[] revised, CancellationToken ct = default)
    {
        var a = await _text.ExtractAllWordsAsync(original, ct);
        var b = await _text.ExtractAllWordsAsync(revised, ct);
        return await Task.Run(() => Compare(a, b, ct), ct);
    }

    internal static DocumentComparison Compare(IReadOnlyList<IReadOnlyList<TextWord>> a, IReadOnlyList<IReadOnlyList<TextWord>> b, CancellationToken ct = default)
    {
        var marks = new List<TextSearchResult>();
        var pages = new List<PageChange>();
        for (var p = 0; p < Math.Max(a.Count, b.Count); p++)
        {
            ct.ThrowIfCancellationRequested();
            var wa = p < a.Count ? a[p] : [];
            var wb = p < b.Count ? b[p] : [];
            var removed = new StringBuilder();
            var added = new StringBuilder();

            if ((long)wa.Count * wb.Count > MaxCells)
            {
                // Too big for a word diff: report the page as changed when its text differs.
                if (!wa.Select(w => w.Text).SequenceEqual(wb.Select(w => w.Text)))
                {
                    removed.Append(string.Join(' ', wa.Select(w => w.Text)));
                    added.Append(string.Join(' ', wb.Select(w => w.Text)));
                    if (wa.Count > 0) marks.Add(Mark(p, "≠", wa));
                }
            }
            else
            {
                foreach (var (kind, fromA, fromB) in Diff(wa, wb))
                {
                    if (kind == '-')
                    {
                        removed.Append(removed.Length > 0 ? " … " : "").Append(string.Join(' ', fromA.Select(w => w.Text)));
                        marks.Add(Mark(p, "− " + string.Join(' ', fromA.Select(w => w.Text)), fromA));
                    }
                    else
                    {
                        var text = string.Join(' ', fromB.Select(w => w.Text));
                        added.Append(added.Length > 0 ? " … " : "").Append(text);
                        // Insertions have no place in A: mark the word they follow (or the first word).
                        if (fromA.Count > 0) marks.Add(Mark(p, "+ " + text, fromA));
                    }
                }
            }
            if (removed.Length > 0 || added.Length > 0)
                pages.Add(new PageChange(p, p < a.Count, p < b.Count, removed.ToString(), added.ToString()));
        }
        return new DocumentComparison(marks, pages);

        static TextSearchResult Mark(int page, string text, IReadOnlyList<TextWord> words) =>
            new() { MatchedText = text, PageIndex = page, Boxes = words.Select(w => w.BoundingBox).ToList() };
    }

    /// <summary>Runs of removed ('-', words of A) and added ('+', words of B plus the A word they follow) words.</summary>
    private static IEnumerable<(char Kind, IReadOnlyList<TextWord> A, IReadOnlyList<TextWord> B)> Diff(IReadOnlyList<TextWord> a, IReadOnlyList<TextWord> b)
    {
        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i].Text == b[j].Text ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var (x, y) = (0, 0);
        var del = new List<TextWord>();
        var ins = new List<TextWord>();
        var lastA = (TextWord?)null;
        IEnumerable<(char, IReadOnlyList<TextWord>, IReadOnlyList<TextWord>)> Flush()
        {
            if (del.Count > 0) { yield return ('-', del.ToList(), []); del.Clear(); }
            if (ins.Count > 0)
            {
                var anchor = lastA ?? (x < n ? a[x] : null);
                yield return ('+', anchor is null ? [] : [anchor], ins.ToList());
                ins.Clear();
            }
        }
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Text == b[y].Text)
            {
                foreach (var r in Flush()) yield return r;
                lastA = a[x];
                x++; y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) ins.Add(b[y++]);
            else del.Add(a[x++]);
        }
        foreach (var r in Flush()) yield return r;
    }
}
