using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Finds the places in a (static) PDF that are meant to be filled in and describes them as form fields:
/// underscore blanks, check-box glyphs and empty white table cells. Nothing is modified; feed the result to
/// <see cref="IPdfFormService.AddFieldsAsync"/>.
/// </summary>
public interface IPdfFormDetector
{
    /// <summary>
    /// Returns suggested fields in reading order, with names derived from nearby labels (and, for table cells,
    /// row label + column header). Areas already covered by an existing form field are skipped.
    /// </summary>
    Task<IReadOnlyList<FormFieldSpec>> DetectFieldsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// The row of at least three equal, adjacent, empty boxes (one character each) containing the point
    /// (<paramref name="nx"/>, <paramref name="ny"/>: normalised, rendered orientation, origin top-left);
    /// null when the point is not inside such a box.
    /// </summary>
    Task<CellRow?> FindCellRowAsync(byte[] pdf, int pageIndex, double nx, double ny, CancellationToken ct = default);
}
