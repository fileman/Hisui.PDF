namespace Hisui.Pdf.Core.Model;

public enum PdfFormFieldType { Text, CheckBox, RadioButton, ComboBox, ListBox, Signature, Unknown }

/// <summary>Snapshot of a single AcroForm field (name, type, current value, options).</summary>
public sealed class PdfFormField
{
    public required string Name { get; init; }
    public PdfFormFieldType Type { get; init; }
    public string? Value { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public bool IsReadOnly { get; init; }
    public bool IsRequired { get; init; }
}
