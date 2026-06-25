using System.IO.Compression;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Reduces PDF size by recompressing embedded JPEG (/DCTDecode) images in place: decode, optionally
/// downscale to a maximum edge, re-encode at a lower quality, and store the smaller result. Text, vector
/// content and the rest of the document structure are preserved, so this is safe to run after OCR.
/// Non-JPEG images and images with a soft mask are left untouched to avoid colour/alpha surprises.
/// </summary>
internal sealed class PdfOptimizer : IPdfOptimizer
{
    public Task<byte[]> OptimizeAsync(byte[] pdf, CompressionOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= new CompressionOptions();

        return Task.Run(() =>
        {
            using var inStream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(inStream, PdfDocumentOpenMode.Modify);

            if (options.DownsampleImages)
            {
                foreach (var obj in doc.Internals.GetAllObjects())
                {
                    ct.ThrowIfCancellationRequested();
                    if (obj is PdfDictionary dict)
                        TryRecompressJpegImage(dict, options);
                }
            }

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }, ct);
    }

    private static void TryRecompressJpegImage(PdfDictionary dict, CompressionOptions opt)
    {
        if (dict.Stream is null) return;
        if (NameOf(dict, "/Subtype") != "/Image") return;
        if (dict.Elements.ContainsKey("/SMask")) return; // leave alpha-masked images intact

        var jpeg = ExtractJpeg(dict);
        if (jpeg is null) return;
        var currentLength = dict.Stream.Value?.Length ?? 0;

        using var bitmap = SKBitmap.Decode(jpeg);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return;

        var (w, h) = FitWithin(bitmap.Width, bitmap.Height, opt.MaxImageEdge);

        SKBitmap? scaled = null;
        try
        {
            var source = bitmap;
            if (w != bitmap.Width || h != bitmap.Height)
            {
                scaled = bitmap.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (scaled is null) return;
                source = scaled;
            }

            using var image = SKImage.FromBitmap(source);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(opt.JpegQuality, 1, 100));
            if (data is null) return;

            var encoded = data.ToArray();
            if (encoded.Length >= currentLength) return; // no gain — keep the original stream

            dict.Stream.Value = encoded;
            dict.Elements["/Filter"] = new PdfName("/DCTDecode"); // store the raw JPEG, dropping any Flate wrapper
            dict.Elements.SetInteger("/Width", w);
            dict.Elements.SetInteger("/Height", h);
            dict.Elements["/ColorSpace"] = new PdfName("/DeviceRGB"); // SkiaSharp emits an RGB JPEG
            dict.Elements.SetInteger("/BitsPerComponent", 8);
            dict.Elements.Remove("/DecodeParms");
            dict.Elements.Remove("/Decode");
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>
    /// Returns the JPEG bytes of a /DCTDecode image, transparently undoing a Flate wrapper
    /// (<c>[/FlateDecode /DCTDecode]</c>, common in scanner output). Null if the image is not a plain JPEG.
    /// </summary>
    private static byte[]? ExtractJpeg(PdfDictionary dict)
    {
        var raw = dict.Stream?.Value;
        if (raw is null || raw.Length == 0) return null;

        var filters = FilterNames(dict);
        return filters switch
        {
            ["/DCTDecode"] => raw,
            ["/FlateDecode", "/DCTDecode"] => Inflate(raw),
            _ => null,
        };
    }

    private static List<string> FilterNames(PdfDictionary dict) => dict.Elements["/Filter"] switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => (e as PdfName)?.Value ?? string.Empty)],
        _ => [],
    };

    private static byte[]? Inflate(byte[] zlib)
    {
        try
        {
            using var input = new MemoryStream(zlib);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null; // not zlib / corrupt — leave the image untouched
        }
    }

    /// <summary>Reads a dictionary entry as a direct PDF name (with leading slash), or null if absent / not a name.</summary>
    private static string? NameOf(PdfDictionary dict, string key) => (dict.Elements[key] as PdfName)?.Value;

    /// <summary>Scales (w, h) down so its longest edge is at most <paramref name="maxEdge"/>; never scales up.</summary>
    private static (int W, int H) FitWithin(int w, int h, int maxEdge)
    {
        var longest = Math.Max(w, h);
        if (maxEdge <= 0 || longest <= maxEdge) return (w, h);
        var scale = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }
}
