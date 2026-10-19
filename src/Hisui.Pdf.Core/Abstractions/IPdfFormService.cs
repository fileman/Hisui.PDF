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

    /// <summary>The form widgets on one page, in /Annots order (empty when there are none).</summary>
    Task<IReadOnlyList<PdfFormWidget>> ReadWidgetsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default);

    /// <summary>Moves/resizes the widget at <paramref name="annotIndex"/> of the page's /Annots to <paramref name="rect"/>
    /// (normalised, rendered orientation).</summary>
    Task<byte[]> SetWidgetRectAsync(byte[] pdf, int pageIndex, int annotIndex, PdfRect rect, CancellationToken ct = default);

    /// <summary>Removes the widget at <paramref name="annotIndex"/>, and its field once no widget of it is left.</summary>
    Task<byte[]> DeleteWidgetAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default);

    /// <summary>Moves/resizes several widgets of one page in one rewrite (key: /Annots index).</summary>
    Task<byte[]> SetWidgetRectsAsync(byte[] pdf, int pageIndex, IReadOnlyDictionary<int, PdfRect> rects, CancellationToken ct = default);

    /// <summary>Removes several widgets of one page (and fields left without widgets).</summary>
    Task<byte[]> DeleteWidgetsAsync(byte[] pdf, int pageIndex, IReadOnlyCollection<int> annotIndices, CancellationToken ct = default);

    /// <summary>Sets the tab order of a page: <paramref name="widgetOrder"/> lists every widget's /Annots index in the new order.</summary>
    Task<byte[]> ReorderWidgetsAsync(byte[] pdf, int pageIndex, IReadOnlyList<int> widgetOrder, CancellationToken ct = default);

    /// <summary>Properties of the field the widget belongs to.</summary>
    Task<FormFieldProperties> ReadPropertiesAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default);

    /// <summary>Applies properties to the field the widget belongs to (rename, flags, font, alignment, format, options).
    /// A name already used by another field throws <see cref="ArgumentException"/>.</summary>
    Task<byte[]> SetPropertiesAsync(byte[] pdf, int pageIndex, int annotIndex, FormFieldProperties properties, CancellationToken ct = default);

    /// <summary>Exports every field value (check boxes as their state name, e.g. "Yes"/"Off").</summary>
    Task<byte[]> ExportDataAsync(byte[] pdf, FormDataFormat format, CancellationToken ct = default);

    /// <summary>Fills the form from exported data; unknown names are ignored.</summary>
    Task<byte[]> ImportDataAsync(byte[] pdf, byte[] data, FormDataFormat format, CancellationToken ct = default);

    /// <summary>Restores the default value (/DV, else empty/unchecked) of the named fields, or of all fields.</summary>
    Task<byte[]> ResetFieldsAsync(byte[] pdf, IReadOnlyCollection<string>? names = null, CancellationToken ct = default);

    /// <summary>Burns the named fields' (or all fields') values into the page as vector content and removes the fields.</summary>
    Task<byte[]> FlattenFieldsAsync(byte[] pdf, IReadOnlyCollection<string>? names = null, CancellationToken ct = default);

    /// <summary>
    /// Fills the named fields (fully qualified names) with the supplied values.
    /// Text/choice fields get the string value; check boxes interpret "true"/"1"/"yes"/"on" (or their export value)
    /// as checked; radio groups select the choice whose export value matches.
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
