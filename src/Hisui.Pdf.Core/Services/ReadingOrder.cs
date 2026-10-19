using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Orders rectangles the way a person reads a page and the way Tab should move through a form:
/// columns first (left column top to bottom, then the next one), rows left to right inside a column.
/// Recursive XY-cut over normalised rects (y down): split at the widest gap no rect crosses (vertical or
/// horizontal), else fall back to row clustering.
/// </summary>
internal static class ReadingOrder
{
    private const double MinColumnGap = 0.015; // ≈ 9 pt on A4: wide enough to be a gutter, not a space between neighbours
    private const double MinRowGap = 0.001;    // ≈ 1 pt: rows of a table are separated by hairlines
    private const double RowTolerance = 0.006; // ≈ 5 pt: same-row centres in the fallback

    /// <param name="extent">Space an item occupies for layout decisions (e.g. field + label); gaps are measured on it.</param>
    /// <param name="rect">The item's own box, used to order items that share a row. Defaults to <paramref name="extent"/>.</param>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, PdfRect> extent, Func<T, PdfRect>? rect = null)
    {
        var list = items.ToList();
        var result = new List<T>(list.Count);
        Cut(list, extent, rect ?? extent, result);
        return result;
    }

    private static void Cut<T>(List<T> items, Func<T, PdfRect> rect, Func<T, PdfRect> own, List<T> output)
    {
        if (items.Count <= 1) { output.AddRange(items); return; }

        // Cut at the single widest empty gutter or row gap (XY-cut): a page-wide band break outranks the
        // small gaps between rows, so a two-column block is split into columns before it is split into rows.
        var vertical = WidestGap(items, i => rect(i).Left, i => rect(i).Right, MinColumnGap);
        var horizontal = WidestGap(items, i => rect(i).Top, i => rect(i).Bottom, MinRowGap);

        var cut = vertical.Gap >= horizontal.Gap ? vertical : horizontal;
        if (cut.Before is not null && cut.After is not null)
        {
            Cut(cut.Before, rect, own, output);
            Cut(cut.After, rect, own, output);
            return;
        }

        // Tangled: cluster into rows by vertical centre, each row left to right.
        var ordered = items.OrderBy(i => (own(i).Top + own(i).Bottom) / 2).ToList();
        var band = new List<T>();
        var bandCentre = 0.0;
        foreach (var item in ordered)
        {
            var centre = (own(item).Top + own(item).Bottom) / 2;
            if (band.Count > 0 && centre - bandCentre > RowTolerance)
            {
                output.AddRange(band.OrderBy(i => own(i).Left));
                band.Clear();
            }
            if (band.Count == 0) bandCentre = centre;
            band.Add(item);
        }
        output.AddRange(band.OrderBy(i => own(i).Left));
    }

    /// <summary>
    /// The widest gap along one axis that no item spans (≥ <paramref name="minGap"/>), with the items
    /// before and after it; Gap is 0 and the lists are null when there is none.
    /// </summary>
    private static (double Gap, List<T>? Before, List<T>? After) WidestGap<T>(
        List<T> items, Func<T, double> start, Func<T, double> end, double minGap)
    {
        var sorted = items.OrderBy(start).ToList();
        var reach = end(sorted[0]);
        var bestGap = 0.0;
        var bestIndex = -1;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = start(sorted[i]) - reach;
            if (gap >= minGap && gap > bestGap) { bestGap = gap; bestIndex = i; }
            reach = Math.Max(reach, end(sorted[i]));
        }
        return bestIndex < 0 ? (0, null, null) : (bestGap, sorted[..bestIndex], sorted[bestIndex..]);
    }
}
