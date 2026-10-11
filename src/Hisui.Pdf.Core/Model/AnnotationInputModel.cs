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
    /// <summary>Form tools: field name (blank = generated). Combo options come one per line in <see cref="Text"/>.</summary>
    public string? FieldName { get; init; }
    public byte[]? ImageBytes { get; init; }
    public WatermarkOptions? WatermarkOptions { get; init; }
}
