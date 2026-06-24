namespace Hisui.Pdf.Core.Model;

/// <summary>Configuration for a diagonal text watermark burned into every page.</summary>
public sealed class WatermarkOptions
{
    /// <summary>Opacity in [0, 1]. Default is 0.25 (subtle but visible).</summary>
    public double Opacity { get; init; } = 0.25;

    /// <summary>Font size in points. Default 64 pt for an A4 page.</summary>
    public double FontSizePoints { get; init; } = 64;

    /// <summary>Counter-clockwise rotation in degrees. Default 45° (diagonal).</summary>
    public double RotationDegrees { get; init; } = 45;

    /// <summary>CSS-style hex color (e.g. "#808080"). Default mid-gray.</summary>
    public string ColorHex { get; init; } = "#808080";
}
