namespace Hisui.Pdf.Core.Model;

/// <summary>Options controlling PDF size reduction (image recompression).</summary>
public sealed record CompressionOptions
{
    /// <summary>Recompress / downsample embedded JPEG images — the main win for scanned PDFs.</summary>
    public bool DownsampleImages { get; init; } = true;

    /// <summary>
    /// Images whose longest edge exceeds this many pixels are scaled down to it (0 = never scale).
    /// 1400 px ≈ 120 DPI for an A4 page — a clear reduction from a typical 150/300 DPI scan while staying legible.
    /// </summary>
    public int MaxImageEdge { get; init; } = 1400;

    /// <summary>JPEG quality (1-100) used when re-encoding recompressed images.</summary>
    public int JpegQuality { get; init; } = 60;
}
