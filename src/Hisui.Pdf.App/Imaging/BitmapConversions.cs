using System.IO;
using Avalonia.Media.Imaging;

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
}
