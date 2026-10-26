namespace Hisui.Pdf.Core.Model;

/// <summary>Collects user-provided parameters for a single annotation operation.</summary>
public sealed record AnnotationInputModel
{
    public required AnnotationTool Tool { get; init; }
    public PdfRect Rect { get; init; }
    public string Text { get; init; } = string.Empty;
    public string ColorHex { get; init; } = "#FFFF00";
    public string? FillHex { get; init; }
    public double Opacity { get; init; } = 0.4;
    public double LineWidth { get; init; } = 2.0;
    public double FontSizePoints { get; init; } = 12;
    /// <summary>FreeText: cell width in points when every character gets its own cell (boxed forms, e.g. IBAN); 0 = normal text.</summary>
    public double CharPitchPoints { get; init; }
    /// <summary>FreeText: draw a light box behind the text; off when typing straight onto a form.</summary>
    public bool Background { get; init; } = true;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    /// <summary>Form tools: field name (blank = generated). Combo options come one per line in <see cref="Text"/>.</summary>
    public string? FieldName { get; init; }
    public byte[]? ImageBytes { get; init; }
    public WatermarkOptions? WatermarkOptions { get; init; }
}
