namespace Hisui.Pdf.Core.Model;

/// <summary>
/// One on-page widget of a form field. <see cref="AnnotIndex"/> is the widget's position in the page's /Annots
/// array, which identifies it for move/resize/delete until the document changes. <see cref="Rect"/> is
/// normalised, in rendered orientation.
/// </summary>
public sealed record PdfFormWidget
{
    /// <summary>Fully qualified field name (parent names joined with '.').</summary>
    public required string Name { get; init; }
    public required PdfFormFieldType Type { get; init; }
    public required int PageIndex { get; init; }
    public required int AnnotIndex { get; init; }
    public required PdfRect Rect { get; init; }
    /// <summary>Text / choice value; "true"/"false" for a check box; the selected export value for a radio group.</summary>
    public string? Value { get; init; }
    /// <summary>Export value this widget sets when checked (check boxes, radio buttons).</summary>
    public string? OnState { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public bool IsReadOnly { get; init; }
    /// <summary>Hover text (/TU).</summary>
    public string? Tooltip { get; init; }
    public bool IsRequired { get; init; }
    public bool IsMultiline { get; init; }
    public int? MaxLength { get; init; }
    public FieldAlignment Alignment { get; init; }
    public FieldFormat Format { get; init; }
    public int Decimals { get; init; } = 2;
    public string DatePattern { get; init; } = "dd/mm/yyyy";
}
