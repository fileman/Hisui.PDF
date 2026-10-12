namespace Hisui.Pdf.Core.Model;

public enum FieldFormat { None, Number, Currency, Percent, Date }

public enum FieldAlignment { Left, Center, Right }

/// <summary>Editable properties of a form field (Acrobat "Properties" dialog subset).</summary>
public sealed record FormFieldProperties
{
    public required string Name { get; init; }
    public PdfFormFieldType Type { get; init; }
    public string? Tooltip { get; init; }
    public bool Required { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>Text fields: allow several lines.</summary>
    public bool Multiline { get; init; }
    /// <summary>Text fields: maximum characters (null = unlimited).</summary>
    public int? MaxLength { get; init; }
    /// <summary>Standard font resource: "Helv" (Helvetica), "TiRo" (Times), "Cour" (Courier).</summary>
    public string FontName { get; init; } = "Helv";
    /// <summary>Points; 0 = auto-fit.</summary>
    public double FontSize { get; init; }
    public FieldAlignment Alignment { get; init; }
    /// <summary>Text fields: how the value is formatted (written as Acrobat AF* format/keystroke actions).</summary>
    public FieldFormat Format { get; init; }
    public int Decimals { get; init; } = 2;
    /// <summary>For <see cref="FieldFormat.Date"/>, an Acrobat date pattern such as "dd/mm/yyyy".</summary>
    public string DatePattern { get; init; } = "dd/mm/yyyy";
    /// <summary>Combo/list entries.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}
