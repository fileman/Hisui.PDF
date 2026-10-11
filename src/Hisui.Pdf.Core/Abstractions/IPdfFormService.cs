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
    /// Creates a new interactive field (text, check box, radio button, combo/list box, signature) on a page.
    /// <paramref name="rect"/> is in rendered orientation, so pages with /Rotate are handled. Creates the
    /// /AcroForm when missing. A blank <paramref name="name"/> gets a unique generated name; a name that
    /// already exists throws <see cref="ArgumentException"/>. <paramref name="options"/> feeds combo/list boxes; for a radio button its first entry is the export value, and an existing radio group of the same name gains a choice.
    /// </summary>
    Task<byte[]> AddFieldAsync(byte[] pdf, int pageIndex, PdfRect rect, PdfFormFieldType type,
        string? name = null, IReadOnlyList<string>? options = null, CancellationToken ct = default);

    /// <summary>Creates many fields in one pass (one document rewrite). Same rules as <see cref="AddFieldAsync"/>.</summary>
    Task<byte[]> AddFieldsAsync(byte[] pdf, IReadOnlyList<FormFieldSpec> specs, CancellationToken ct = default);

    /// <summary>
    /// Rasterises every page (300 DPI, with form-fill rendered) and rebuilds the PDF as image pages,
    /// removing all interactive form infrastructure.
    /// </summary>
    Task<byte[]> FlattenAsync(byte[] pdf, CancellationToken ct = default);
}
