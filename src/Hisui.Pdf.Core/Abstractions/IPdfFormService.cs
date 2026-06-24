using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// AcroForm operations: read, fill and flatten interactive form fields.
/// Flatten replaces each page with a raster image (form values visible, no longer editable).
/// </summary>
public interface IPdfFormService
{
    /// <summary>Returns all AcroForm fields with their current values. Empty list if the PDF has no form.</summary>
    Task<IReadOnlyList<PdfFormField>> ReadFieldsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// Fills the named fields with the supplied values.
    /// Text fields get the string value; check boxes interpret "true"/"1"/"yes"/"on" as checked.
    /// Fields not present in <paramref name="values"/> are left unchanged.
    /// </summary>
    Task<byte[]> FillFieldsAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);

    /// <summary>
    /// Rasterises every page (300 DPI, with form-fill rendered) and rebuilds the PDF as image pages,
    /// removing all interactive form infrastructure.
    /// </summary>
    Task<byte[]> FlattenAsync(byte[] pdf, CancellationToken ct = default);
}
