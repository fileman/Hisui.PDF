namespace Hisui.Pdf.Core.Model;

/// <summary>A form field to create (or detected as creatable). <see cref="Rect"/> is normalised, in rendered orientation.</summary>
public sealed record FormFieldSpec
{
    public required int PageIndex { get; init; }
    public required PdfRect Rect { get; init; }
    public required PdfFormFieldType Type { get; init; }
    /// <summary>Blank = generated. For radio buttons, same name = same group.</summary>
    public string? Name { get; init; }
    /// <summary>Combo/list entries; for a radio button the first entry is the choice's export value.</summary>
    public IReadOnlyList<string>? Options { get; init; }
    /// <summary>Hover text shown by viewers (/TU).</summary>
    public string? Tooltip { get; init; }
}
