namespace Hisui.Pdf.Core.Model;

/// <summary>
/// An annotation burned into a page by <see cref="Abstractions.IPdfAnnotationService"/> and read back from its tag, so it
/// can be moved, edited or removed later. Only the properties of <see cref="Tool"/> are meaningful; the rest keep their
/// defaults. <see cref="Rect"/> is in normalised visual page coordinates.
/// </summary>
public sealed record PlacedAnnotation
{
    public required string Id { get; init; }
    public required AnnotationTool Tool { get; init; }
    public required PdfRect Rect { get; init; }
    public string Text { get; init; } = string.Empty;
    public double FontSizePoints { get; init; } = 12;
    public string ColorHex { get; init; } = "#000000";
    public string? FillHex { get; init; }
    public double Opacity { get; init; } = 1;
    public double LineWidth { get; init; } = 2;
    public double CharPitchPoints { get; init; }
    public bool Background { get; init; } = true;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    /// <summary>The original PNG/JPEG bytes of an image overlay, so it can be redrawn elsewhere.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>The input that redraws this annotation as it is (change <see cref="AnnotationInputModel.Rect"/> to move it).</summary>
    public AnnotationInputModel ToInputModel() => new()
    {
        Tool = Tool,
        Rect = Rect,
        Text = Text,
        ColorHex = ColorHex,
        FillHex = FillHex,
        Opacity = Opacity,
        LineWidth = LineWidth,
        FontSizePoints = FontSizePoints,
        CharPitchPoints = CharPitchPoints,
        Background = Background,
        Bold = Bold,
        Italic = Italic,
        ImageBytes = ImageBytes,
    };
}
