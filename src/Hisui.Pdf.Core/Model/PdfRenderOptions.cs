namespace Hisui.Pdf.Core.Model;

/// <summary>Colour handling for a page render.</summary>
public enum PdfRenderColorMode
{
    /// <summary>Full colour, as the document defines it.</summary>
    Color,

    /// <summary>Everything converted to shades of grey.</summary>
    Grayscale,

    /// <summary>All text drawn solid black; images and vector art keep their colours.</summary>
    BlackText,
}

/// <summary>Optional render settings beyond page and resolution.</summary>
/// <param name="ColorMode">Colour handling.</param>
/// <param name="ForPrinting">Render as for a printer: honours print-only / no-print annotation flags.</param>
public sealed record PdfRenderOptions(PdfRenderColorMode ColorMode = PdfRenderColorMode.Color, bool ForPrinting = false)
{
    public static PdfRenderOptions Default { get; } = new();
}

/// <summary>A page's displayed size in points (1/72 inch), with its /Rotate already applied.</summary>
public readonly record struct PdfPageSize(double Width, double Height);
