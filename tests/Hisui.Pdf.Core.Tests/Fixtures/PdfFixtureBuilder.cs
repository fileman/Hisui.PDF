using Hisui.Pdf.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Hisui.Pdf.Core.Tests.Fixtures;

/// <summary>
/// Builds small, deterministic PDFs for tests. Pages are drawn with vector shapes only (no text), so
/// the PDFsharp "Core" build needs no font resolver. A text-capable variant is added in the
/// extraction phase, where a bundled font resolver is wired up.
/// </summary>
public static class PdfFixtureBuilder
{
    /// <summary>Creates an A4 PDF with <paramref name="pageCount"/> pages, each visually distinct.</summary>
    public static byte[] Create(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        using var document = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;

            using var gfx = XGraphics.FromPdfPage(page);
            // A filled bar whose position shifts per page — enough to tell pages apart after round-trips.
            gfx.DrawRectangle(XBrushes.SteelBlue, 40, 40 + (i * 12 % 200), 160, 60);
            gfx.DrawRectangle(XPens.Black, 20, 20, page.Width.Point - 40, page.Height.Point - 40);
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Creates an A4 PDF with <paramref name="pageCount"/> pages, each containing the supplied text.
    /// Requires the Windows font resolver — configured automatically via <see cref="PdfAnnotationService"/>'s
    /// static constructor (which fires when the test assembly first references that type).
    /// </summary>
    public static byte[] CreateWithText(string text, int pageCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        // Ensure font resolver is active (harmless no-op if already set by PdfAnnotationService's static ctor).
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();

        using var document = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14);
            gfx.DrawString($"{text} (p{i + 1})", font, XBrushes.Black, new XPoint(50, 100 + i * 20));
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a single-page A4 PDF that contains a raster image but no text layer — i.e. it looks
    /// like a scanned page to the OCR scanned-page detector.
    /// </summary>
    public static byte[] CreateScanned(int rotate = 0, int cropInset = 0)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        if (rotate != 0) page.Rotate = rotate; // exercises rotated-scan handling in the OCR text layer
        if (cropInset != 0) // a CropBox smaller than the MediaBox — exercises crop-aware text placement
            page.CropBox = new PdfRectangle(
                new XPoint(cropInset, cropInset),
                new XPoint(page.Width.Point - cropInset, page.Height.Point - cropInset));

        using (var gfx = XGraphics.FromPdfPage(page))
        using (var imgStream = new MemoryStream(OnePixelPng))
        {
            var image = XImage.FromStream(imgStream);
            // Stretch the 1×1 image across most of the page so it reads as page content.
            gfx.DrawImage(image, 40, 40, page.Width.Point - 80, page.Height.Point - 80);
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A scanned letter as office scanners make them: an A4 page covered by horizontal image strips (white paper
    /// with one solid "logo" block at the top left) and a real, rotated protocol stamp in the left margin, so
    /// the page has a text layer but no horizontal text.
    /// </summary>
    public const string ScannedLetterStamp = "Protocollo N. 96548/2026 del 30-10-2026";

    public static byte[] CreateScannedLetterWithStamp()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        double w = page.Width.Point, h = page.Height.Point;

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            const int strips = 3;
            for (var s = 0; s < strips; s++)
            {
                // 100 DPI strips: 827 px wide, a third of the page tall each.
                using var bmp = new SKBitmap(827, 390);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.White);
                    if (s == 0)
                        using (var paint = new SKPaint { Color = SKColors.Black })
                            canvas.DrawRect(80, 40, 120, 120, paint); // the logo: ≈ 1.2" square
                }
                using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(png.ToArray());
                gfx.DrawImage(XImage.FromStream(stream), 0, s * h / strips, w, h / strips);
            }

            var font = new XFont("Arial", 8);
            var origin = new XPoint(20, h - 300);
            gfx.RotateAtTransform(-90, origin);
            gfx.DrawString(ScannedLetterStamp, font, XBrushes.Gray, origin);
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A digital A4 page: a left-aligned paragraph, a right-aligned date line, an e-mail address, a numbered list
    /// on consecutive lines, and one picture embedded as three stacked PNG strips (x 100–300 pt, y 400–550 pt).
    /// </summary>
    public static byte[] CreateDigitalLetterWithStrips()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var right = page.Width.Point - 72;

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 11);
            gfx.DrawString("Opening line of the letter, flush with the left margin and long enough.", font, XBrushes.Black, new XPoint(72, 100));
            var date = "Macerata, 30.10.2026";
            gfx.DrawString(date, font, XBrushes.Black, new XPoint(right - gfx.MeasureString(date, font).Width, 140));
            gfx.DrawString("Write to office@example.org for details.", font, XBrushes.Black, new XPoint(72, 200));
            gfx.DrawString("1. First item", font, XBrushes.Black, new XPoint(90, 240));
            gfx.DrawString("2. Second item", font, XBrushes.Black, new XPoint(90, 254));
            gfx.DrawString("Last line of the page, also flush with the left margin, to fix the right edge.", font, XBrushes.Black, new XPoint(72, 700));

            for (var s = 0; s < 3; s++)
            {
                using var bmp = new SKBitmap(200, 50);
                using (var canvas = new SKCanvas(bmp)) canvas.Clear(s == 1 ? SKColors.SteelBlue : SKColors.Orange);
                using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(png.ToArray());
                gfx.DrawImage(XImage.FromStream(stream), 100, 400 + s * 50, 200, 50);
            }
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a single-page A4 PDF that embeds a sizeable baseline-JPEG image, so the optimizer's
    /// <c>/DCTDecode</c> recompression path has something real to shrink.
    /// </summary>
    public static byte[] CreateWithJpegImage(int sizePx = 1600, int jpegQuality = 92)
    {
        using var bmp = new SKBitmap(sizePx, sizePx);
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.White);
            for (var y = 0; y < sizePx; y += 5)
            {
                paint.Color = new SKColor((byte)(y % 256), (byte)((y * 3) % 256), (byte)((y * 7) % 256));
                canvas.DrawLine(0, y, sizePx, y, paint);
            }
        }
        using var skImage = SKImage.FromBitmap(bmp);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
        var jpeg = data.ToArray();

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var stream = new MemoryStream(jpeg))
        {
            var image = XImage.FromStream(stream);
            gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a single-page A4 PDF that embeds a sizeable opaque PNG, which PDFsharp stores as a
    /// Flate-encoded raster image — exercising the optimizer's <c>/FlateDecode</c> recompression path.
    /// </summary>
    public static byte[] CreateWithFlateImage(int sizePx = 1000)
    {
        // A high-frequency, low-redundancy pattern: Flate can barely compress it (so the stored image
        // stays large), which is what a real photo / scan looks like — and what makes JPEG a clear win.
        using var bmp = new SKBitmap(new SKImageInfo(sizePx, sizePx, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new SKColor[sizePx * sizePx];
        for (var y = 0; y < sizePx; y++)
            for (var x = 0; x < sizePx; x++)
                pixels[(y * sizePx) + x] = new SKColor(
                    (byte)((x * 7) + (y * 13)),
                    (byte)((x * 31) ^ (y * 17)),
                    (byte)(((x + y) * 29) + (x * 3)));
        bmp.Pixels = pixels;

        using var skImage = SKImage.FromBitmap(bmp);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var stream = new MemoryStream(png))
        {
            var image = XImage.FromStream(stream);
            gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    // A minimal valid 1×1 opaque PNG (white pixel).
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
