using System.IO.Compression;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Reduces PDF size in two complementary ways: (1) recompresses embedded raster images in place —
/// JPEG (<c>/DCTDecode</c>) <em>and</em> Flate-encoded bitmaps (<c>/FlateDecode</c>, the form most
/// born-digital and many scanned PDFs use) — by decoding, optionally downscaling to a maximum edge and
/// re-encoding as a lower-quality JPEG; and (2) re-deflates content streams at best compression on save.
/// Text, vector content and the document structure are preserved, so this is safe to run after OCR.
/// Images with a soft mask / colour key / explicit decode array, image masks, and colour spaces we cannot
/// faithfully decode (indexed, CMYK, separation) are left untouched to avoid colour / alpha surprises.
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
                        TryRecompressImage(dict, options);
                }
            }

            // Re-deflate content streams (and prefer the smaller result) on save. This is the only win
            // for image-free text / vector PDFs, and never corrupts content.
            doc.Options.NoCompression = false;
            doc.Options.CompressContentStreams = true;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }, ct);
    }

    /// <summary>
    /// Recompresses a single image XObject in place if it is a colour/grayscale bitmap we can safely
    /// decode and the re-encoded JPEG is smaller. No-ops for anything risky or already optimal.
    /// </summary>
    private static void TryRecompressImage(PdfDictionary dict, CompressionOptions opt)
    {
        if (dict.Stream is null) return;
        if (NameOf(dict, "/Subtype") != "/Image") return;
        if (dict.Elements.ContainsKey("/SMask")) return;   // soft-masked (alpha) — re-encode would drop it
        if (dict.Elements.ContainsKey("/Mask")) return;    // colour-key / stencil mask
        if (TrueFlag(dict, "/ImageMask")) return;          // 1-bit stencil, not a colour bitmap
        if (dict.Elements.ContainsKey("/Decode")) return;  // custom sample remapping — leave intact

        var currentLength = dict.Stream.Value?.Length ?? 0;
        if (currentLength == 0) return;

        // Enforce the safe colour-space subset for EVERY path (JPEG included): only DeviceGray (1) and
        // DeviceRGB / ICCBased N=3 (3) decode to the RGB we re-encode and stamp as /DeviceRGB. Anything else
        // (CMYK, indexed, separation, DeviceN, Lab) would be silently re-coloured, so leave it untouched.
        var components = ComponentCount(dict);
        if (components is not (1 or 3)) return;

        using var source = DecodeImage(dict, components);
        if (source is null || source.Width <= 0 || source.Height <= 0) return;

        var (w, h) = FitWithin(source.Width, source.Height, opt.MaxImageEdge);

        SKBitmap? scaled = null;
        try
        {
            var bitmap = source;
            if (w != source.Width || h != source.Height)
            {
                scaled = source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (scaled is null) return;
                bitmap = scaled;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(opt.JpegQuality, 1, 100));
            if (data is null) return;

            var encoded = data.ToArray();
            if (encoded.Length >= currentLength) return; // no gain — keep the original stream

            dict.Stream.Value = encoded;
            dict.Elements["/Filter"] = new PdfName("/DCTDecode"); // store the raw JPEG, dropping any wrapper
            dict.Elements.SetInteger("/Width", w);
            dict.Elements.SetInteger("/Height", h);
            dict.Elements["/ColorSpace"] = new PdfName("/DeviceRGB"); // SkiaSharp emits an RGB JPEG
            dict.Elements.SetInteger("/BitsPerComponent", 8);
            dict.Elements.Remove("/DecodeParms");
            dict.Elements.Remove("/DP");
            dict.Elements.Remove("/Decode");
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>
    /// Decodes an image XObject's pixels, by filter chain. Null if it is not a supported bitmap.
    /// <paramref name="components"/> is the already-validated colour component count (1 or 3).
    /// </summary>
    private static SKBitmap? DecodeImage(PdfDictionary dict, int components)
    {
        var raw = dict.Stream!.Value;
        if (raw is null || raw.Length == 0) return null;

        return FilterNames(dict) switch
        {
            ["/DCTDecode"] => SafeDecode(raw),
            ["/FlateDecode", "/DCTDecode"] => SafeDecode(Inflate(raw)), // scanner JPEG inside a Flate wrapper
            ["/FlateDecode"] => DecodeFlateRaster(dict, raw, components),
            _ => null,
        };
    }

    private static SKBitmap? SafeDecode(byte[]? jpeg) => jpeg is null ? null : SKBitmap.Decode(jpeg);

    /// <summary>
    /// Decodes a Flate-encoded raster image (raw 8-bit samples, optionally PNG/TIFF predictor-filtered)
    /// in DeviceRGB / DeviceGray / ICCBased(N=1|3). Returns null for anything outside that safe subset.
    /// </summary>
    private static SKBitmap? DecodeFlateRaster(PdfDictionary dict, byte[] flate, int components)
    {
        if (dict.Elements.GetInteger("/BitsPerComponent") != 8) return null;

        var width = dict.Elements.GetInteger("/Width");
        var height = dict.Elements.GetInteger("/Height");
        if (width <= 0 || height <= 0) return null;

        var raw = Inflate(flate);
        if (raw is null) return null;

        var parms = DecodeParmsOf(dict);
        var predictor = parms?.Elements.GetInteger("/Predictor") ?? 1;
        if (predictor > 1)
        {
            var colors = GetIntOrDefault(parms!, "/Colors", 1);
            var columns = GetIntOrDefault(parms!, "/Columns", 1);
            var bpc = GetIntOrDefault(parms!, "/BitsPerComponent", 8);
            if (bpc != 8 || colors != components || columns != width) return null; // can't decode reliably
            raw = UndoPredictor(raw, predictor, colors, columns);
            if (raw is null) return null;
        }

        long expected = (long)width * height * components;
        if (raw.Length < expected) return null;

        return BuildBitmap(raw, width, height, components);
    }

    /// <summary>
    /// Reverses a PNG (per-row filter, predictor 10-15) or TIFF (horizontal differencing, predictor 2)
    /// predictor over 8-bit samples. Returns the tightly packed samples, or null for an unsupported predictor.
    /// </summary>
    internal static byte[]? UndoPredictor(byte[] data, int predictor, int colors, int columns)
    {
        var bpp = colors;                 // bytes per pixel at 8 bits/component
        var stride = colors * columns;    // bytes per output row
        if (stride <= 0) return null;

        if (predictor == 2) // TIFF horizontal differencing
        {
            var rows = data.Length / stride;
            var outBuf = new byte[rows * stride];
            Array.Copy(data, outBuf, outBuf.Length);
            for (var r = 0; r < rows; r++)
            {
                var off = r * stride;
                for (var i = bpp; i < stride; i++)
                    outBuf[off + i] = (byte)(outBuf[off + i] + outBuf[off + i - bpp]);
            }
            return outBuf;
        }

        if (predictor >= 10) // PNG predictors — each row carries a leading filter-type byte
        {
            var inRow = stride + 1;
            var rows = data.Length / inRow;
            var outBuf = new byte[rows * stride];
            var prev = new byte[stride];
            for (var r = 0; r < rows; r++)
            {
                var ft = data[r * inRow];
                var inOff = r * inRow + 1;
                var outOff = r * stride;
                for (var i = 0; i < stride; i++)
                {
                    int x = data[inOff + i];
                    int a = i >= bpp ? outBuf[outOff + i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    var val = ft switch
                    {
                        0 => x,
                        1 => x + a,
                        2 => x + b,
                        3 => x + ((a + b) >> 1),
                        4 => x + Paeth(a, b, c),
                        _ => x,
                    };
                    outBuf[outOff + i] = (byte)val;
                }
                Array.Copy(outBuf, outOff, prev, 0, stride);
            }
            return outBuf;
        }

        return null; // predictors 3-9 are not used in practice
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static SKBitmap BuildBitmap(byte[] raw, int width, int height, int components)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        var pixels = new SKColor[width * height];

        if (components == 3)
        {
            for (var p = 0; p < pixels.Length; p++)
            {
                var s = p * 3;
                pixels[p] = new SKColor(raw[s], raw[s + 1], raw[s + 2]);
            }
        }
        else // grayscale
        {
            for (var p = 0; p < pixels.Length; p++)
            {
                var g = raw[p];
                pixels[p] = new SKColor(g, g, g);
            }
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    /// <summary>Number of colour components for a supported colour space, else 0 (unsupported → skip).</summary>
    private static int ComponentCount(PdfDictionary dict)
    {
        var cs = Resolve(dict.Elements["/ColorSpace"]);
        switch (cs)
        {
            case PdfName name:
                return name.Value switch
                {
                    "/DeviceRGB" or "/RGB" => 3,
                    "/DeviceGray" or "/G" or "/CalGray" => 1,
                    _ => 0, // DeviceCMYK, indexed/abbreviated, pattern, etc. — not safe to re-encode
                };
            case PdfArray array when array.Elements.Count >= 2
                                     && (Resolve(array.Elements[0]) as PdfName)?.Value == "/ICCBased":
                var profile = array.Elements.GetDictionary(1);
                return profile?.Elements.GetInteger("/N") ?? 0;
            default:
                return 0;
        }
    }

    /// <summary>The /DecodeParms (or /DP) entry as a dictionary, resolving a single-element array form.</summary>
    private static PdfDictionary? DecodeParmsOf(PdfDictionary dict)
    {
        var parms = dict.Elements.GetDictionary("/DecodeParms") ?? dict.Elements.GetDictionary("/DP");
        if (parms is not null) return parms;

        if (dict.Elements["/DecodeParms"] is PdfArray a && a.Elements.Count > 0)
            return a.Elements.GetDictionary(0);
        return null;
    }

    private static int GetIntOrDefault(PdfDictionary dict, string key, int fallback) =>
        dict.Elements.ContainsKey(key) ? dict.Elements.GetInteger(key) : fallback;

    private static List<string> FilterNames(PdfDictionary dict) => Resolve(dict.Elements["/Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => (Resolve(e) as PdfName)?.Value ?? string.Empty)],
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

    private static bool TrueFlag(PdfDictionary dict, string key) =>
        dict.Elements.ContainsKey(key) && dict.Elements.GetBoolean(key);

    /// <summary>Reads a dictionary entry as a direct PDF name (with leading slash), or null if absent / not a name.</summary>
    private static string? NameOf(PdfDictionary dict, string key) => Resolve(dict.Elements[key]) as PdfName is { } n ? n.Value : null;

    /// <summary>Follows an indirect reference to its value; passes direct items through unchanged.</summary>
    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>Scales (w, h) down so its longest edge is at most <paramref name="maxEdge"/>; never scales up.</summary>
    private static (int W, int H) FitWithin(int w, int h, int maxEdge)
    {
        var longest = Math.Max(w, h);
        if (maxEdge <= 0 || longest <= maxEdge) return (w, h);
        var scale = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }
}
