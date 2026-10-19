namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A rasterised PDF page as raw pixels: 8-bit BGRA, top row first, composited over an opaque white
/// background (every pixel has alpha 255, so it is both straight and premultiplied).
/// </summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row (<c>Width * 4</c>).</param>
/// <param name="Bgra">The pixel buffer, <c>Stride * Height</c> bytes. Treat as read-only.</param>
public sealed record RenderedPage(int Width, int Height, int Stride, byte[] Bgra);
