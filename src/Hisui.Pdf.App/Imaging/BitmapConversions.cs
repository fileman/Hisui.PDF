using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Imaging;

public static class BitmapConversions
{
    /// <summary>
    /// Decodes PNG bytes into an Avalonia <see cref="Bitmap"/>. Safe to call on background threads;
    /// the resulting bitmap is immutable and can be bound directly from the UI thread.
    /// </summary>
    public static Bitmap ToFrozenBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    /// <summary>
    /// Builds an Avalonia <see cref="Bitmap"/> straight from a renderer's raw BGRA pixels (no PNG
    /// round trip). The pixels are copied, so <paramref name="page"/> can be discarded afterwards. Safe
    /// to call on background threads. The page is rendered at <paramref name="page"/>'s pixel size with
    /// the standard 96 DPI metadata, matching what decoding the PNG produced.
    /// </summary>
    public static Bitmap ToFrozenBitmap(RenderedPage page)
    {
        var pixels = GCHandle.Alloc(page.Bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque,
                pixels.AddrOfPinnedObject(),
                new PixelSize(page.Width, page.Height),
                new Vector(96, 96),
                page.Stride);
        }
        finally
        {
            pixels.Free();
        }
    }
}
