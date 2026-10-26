namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A free text drawn by <see cref="Abstractions.IPdfAnnotationService.AddFreeTextAsync"/>, read back from the page so it
/// can be edited, moved or removed later. <see cref="Rect"/> is in normalised visual page coordinates.
/// </summary>
public sealed record FreeTextInfo(
    string Id,
    PdfRect Rect,
    string Text,
    double FontSizePoints,
    string ColorHex,
    double CharPitchPoints,
    bool Background);
