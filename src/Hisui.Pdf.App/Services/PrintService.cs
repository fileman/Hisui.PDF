using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Printing;

namespace Hisui.Pdf.App.Services;

/// <inheritdoc />
internal sealed class PrintService : IPrintService
{
    // Rasterisation DPI for the print path: high enough for crisp text/lines, low enough to stay fast and
    // within driver spool limits. Pages printed smaller (n-up) or larger (poster) scale this by the same
    // factor so the dots per printed inch stay constant.
    private const int PrintDpi = 200;

    // Upper bound on one page raster (~240 MB of BGRA), which large poster scales would otherwise exceed.
    private const double MaxRasterPixels = 60_000_000;

    private const double HundredthsToPoints = 0.72;

    private readonly IPdfRenderer _renderer;

    public PrintService(IPdfRenderer renderer) => _renderer = renderer;

    public bool SupportsSystemDialog => OperatingSystem.IsWindows();

    public string? DefaultPrinter => OperatingSystem.IsWindows() ? DefaultPrinterWindows() : null;

    public IReadOnlyList<string> GetPrinters() =>
        OperatingSystem.IsWindows() ? InstalledPrintersWindows() : [];

    public bool SupportsDuplex(string printerName) =>
        OperatingSystem.IsWindows() && !string.IsNullOrEmpty(printerName) && CanDuplexWindows(printerName);

    public PrinterCapabilities GetCapabilities(string printerName) =>
        OperatingSystem.IsWindows() && !string.IsNullOrEmpty(printerName)
            ? CapabilitiesWindows(printerName)
            : PrinterCapabilities.Fallback;

    public PrinterDriverSettings? ShowPrinterProperties(string printerName, IntPtr ownerHandle, byte[]? current) =>
        OperatingSystem.IsWindows() && !string.IsNullOrEmpty(printerName)
            ? PrinterPropertiesWindows(printerName, ownerHandle, current)
            : null;

    public Task<bool> PrintAsync(byte[] pdf, PrintJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(job);

        if (!OperatingSystem.IsWindows())
            return PrintViaShellAsync(pdf, ct); // no rich path off Windows

        if (job.Sheets.Count == 0 || string.IsNullOrEmpty(job.PrinterName))
            return Task.FromResult(false);

        return Task.Run(async () =>
        {
            // Re-assert the platform inside the closure: the outer guard doesn't flow into the lambda,
            // which is what the windows-only PrintDocument API needs to see.
            if (!OperatingSystem.IsWindows()) return false;
            var sizes = await _renderer.GetPageSizesAsync(pdf, ct).ConfigureAwait(false);
            return PrintWindows(pdf, sizes, job, ct);
        }, ct);
    }

    public async Task<bool> PrintViaShellAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // Persist to a temp file the OS print handler can read. Left for the OS to clean up — deleting it
        // immediately would race the (often out-of-process) handler that prints it.
        var temp = Path.Combine(Path.GetTempPath(), $"hisui-print-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(temp, pdf, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            // The shell "print" verb invokes the default PDF handler's print action; fall back to opening it.
            return TryStart(new ProcessStartInfo(temp) { Verb = "print", UseShellExecute = true })
                || TryStart(new ProcessStartInfo(temp) { UseShellExecute = true });
        }

        if (OperatingSystem.IsMacOS())
            return TryStart(new ProcessStartInfo("open", Quote(temp)));

        // Linux / other: open in the default viewer so the user prints from its dialog, else send to CUPS.
        return TryStart(new ProcessStartInfo("xdg-open", Quote(temp)))
            || TryStart(new ProcessStartInfo("lp", Quote(temp)));
    }

    // ── Windows print stack (System.Drawing.Printing) ─────────────────────────

    [SupportedOSPlatform("windows")]
    private bool PrintWindows(byte[] pdf, IReadOnlyList<PdfPageSize> sizes, PrintJob job, CancellationToken ct)
    {
        using var pd = new PrintDocument();
        pd.DocumentName = "Hisui PDF";
        pd.PrinterSettings.PrinterName = job.PrinterName;
        if (!pd.PrinterSettings.IsValid) return false;

        if (job.DriverSettings is { Length: > 0 } devMode)
            ApplyDevMode(pd.PrinterSettings, pd.DefaultPageSettings, devMode);

        pd.PrinterSettings.Copies = (short)Math.Clamp(job.Copies, 1, 99);
        pd.PrinterSettings.Collate = true;
        if (pd.PrinterSettings.CanDuplex)
            pd.PrinterSettings.Duplex = job.Duplex switch
            {
                PrintDuplex.LongEdge => Duplex.Vertical,
                PrintDuplex.ShortEdge => Duplex.Horizontal,
                _ => Duplex.Simplex,
            };

        // Greyscale also tells the driver, so colour devices print with black toner only.
        if (job.ColorMode == PdfRenderColorMode.Grayscale) pd.DefaultPageSettings.Color = false;

        var source = FindSource(pd.PrinterSettings, job.PaperSource);
        if (source is not null) pd.DefaultPageSettings.PaperSource = source;

        // Coordinates start at the printable area's corner, matching the layout's printable-area space.
        pd.OriginAtMargins = false;

        var render = new PdfRenderOptions(job.ColorMode, ForPrinting: true);
        using var cache = new PageImageCache();
        var cursor = 0;

        pd.QueryPageSettings += (_, e) =>
        {
            var sheet = job.Sheets[cursor];
            e.PageSettings.Landscape = sheet.Landscape;
            var paper = FindPaper(pd.PrinterSettings, sheet.Paper.Name);
            if (paper is not null) e.PageSettings.PaperSize = paper;
            if (source is not null) e.PageSettings.PaperSource = source;
        };

        pd.PrintPage += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            DrawSheet(e.Graphics!, job.Sheets[cursor], pdf, sizes, render, cache, ct);
            cursor++;
            e.HasMorePages = cursor < job.Sheets.Count;
        };

        pd.Print();
        return true;
    }

    [SupportedOSPlatform("windows")]
    private void DrawSheet(
        Graphics g, PrintSheet sheet, byte[] pdf, IReadOnlyList<PdfPageSize> sizes,
        PdfRenderOptions render, PageImageCache cache, CancellationToken ct)
    {
        // VisibleClipBounds is the device's printable area in 1/100 inch for this page's orientation. The
        // layout used the paper's nominal printable area, so map it onto the real one (uniform, centred) —
        // a few hundredths of an inch of difference must never clip content.
        var area = g.VisibleClipBounds;
        var sheetW = sheet.Size.Width / HundredthsToPoints;
        var sheetH = sheet.Size.Height / HundredthsToPoints;
        var fit = Math.Min(area.Width / sheetW, area.Height / sheetH);
        var k = (float)(fit / HundredthsToPoints); // points → page units

        g.TranslateTransform(
            (float)(area.X + (area.Width - sheetW * fit) / 2),
            (float)(area.Y + (area.Height - sheetH * fit) / 2));
        g.ScaleTransform(k, k);
        g.SetClip(new RectangleF(0, 0, (float)sheet.Size.Width, (float)sheet.Size.Height));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        foreach (var placement in sheet.Placements)
        {
            var page = sizes[placement.PageIndex];
            var b = placement.Bounds;
            var shown = placement.Rotated ? b.Height : b.Width; // the box side the page's width maps to
            var dpi = PrintDpi * shown / page.Width;
            dpi = Math.Min(dpi, 72 * Math.Sqrt(MaxRasterPixels / (page.Width * page.Height)));
            var image = cache.Get(placement.PageIndex, (int)Math.Clamp(Math.Round(dpi), 36, 1200),
                (index, d) => _renderer.RenderPageAsync(pdf, index, d, render, ct).GetAwaiter().GetResult());

            var state = g.Save();
            if (placement.Rotated)
            {
                // 90° clockwise: the page's top edge lands on the box's right edge.
                g.TranslateTransform((float)b.Right, (float)b.Y);
                g.RotateTransform(90);
                g.DrawImage(image, new RectangleF(0, 0, (float)b.Height, (float)b.Width));
            }
            else
            {
                g.DrawImage(image, new RectangleF((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height));
            }
            g.Restore(state);

            if (placement.Border)
            {
                using var pen = new Pen(Color.FromArgb(110, 110, 110), 0.5f);
                g.DrawRectangle(pen, (float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
            }
        }

        if (sheet.Marks.Count > 0)
        {
            using var pen = new Pen(Color.FromArgb(150, 150, 150), 0.5f) { DashPattern = [6, 4] };
            foreach (var m in sheet.Marks)
                g.DrawLine(pen, (float)m.X1, (float)m.Y1, (float)m.X2, (float)m.Y2);
        }

        if (sheet.Labels.Count > 0)
        {
            using var font = new Font("Segoe UI", 7f, GraphicsUnit.World);
            using var back = new SolidBrush(Color.White);
            foreach (var label in sheet.Labels)
            {
                var size = g.MeasureString(label.Text, font);
                g.FillRectangle(back, (float)label.X - 1, (float)label.Y, size.Width + 2, size.Height);
                g.DrawString(label.Text, font, Brushes.DimGray, (float)label.X, (float)label.Y);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static PaperSize? FindPaper(PrinterSettings settings, string name)
    {
        foreach (PaperSize paper in settings.PaperSizes)
            if (string.Equals(paper.PaperName, name, StringComparison.OrdinalIgnoreCase))
                return paper;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static PaperSource? FindSource(PrinterSettings settings, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (PaperSource source in settings.PaperSources)
            if (string.Equals(source.SourceName, name, StringComparison.OrdinalIgnoreCase))
                return source;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static PrinterCapabilities CapabilitiesWindows(string printerName)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printerName };
            if (!settings.IsValid) return PrinterCapabilities.Fallback;

            var sources = new List<string>();
            foreach (PaperSource source in settings.PaperSources)
                if (!string.IsNullOrWhiteSpace(source.SourceName) && !sources.Contains(source.SourceName, StringComparer.OrdinalIgnoreCase))
                    sources.Add(source.SourceName);

            var page = settings.DefaultPageSettings;
            var margin = HardMargin(page);

            var papers = new List<PrinterPaper>();
            foreach (PaperSize paper in settings.PaperSizes)
            {
                if (paper.Width <= 0 || paper.Height <= 0 || string.IsNullOrWhiteSpace(paper.PaperName)) continue;
                if (papers.Exists(p => string.Equals(p.Name, paper.PaperName, StringComparison.OrdinalIgnoreCase))) continue;
                var w = paper.Width * HundredthsToPoints;
                var h = paper.Height * HundredthsToPoints;
                papers.Add(new PrinterPaper(paper.PaperName, Math.Min(w, h), Math.Max(w, h), margin));
            }
            if (papers.Count == 0) return PrinterCapabilities.Fallback with { CanDuplex = settings.CanDuplex };

            var defaultName = page.PaperSize?.PaperName;
            var defaultIndex = papers.FindIndex(p => string.Equals(p.Name, defaultName, StringComparison.OrdinalIgnoreCase));

            return new PrinterCapabilities(settings.CanDuplex, settings.SupportsColor, sources, papers, Math.Max(0, defaultIndex));
        }
        catch
        {
            return PrinterCapabilities.Fallback; // a misbehaving driver shouldn't break the dialog
        }
    }

    /// <summary>The largest unprintable border of the default paper, in points.</summary>
    [SupportedOSPlatform("windows")]
    private static double HardMargin(PageSettings page)
    {
        try
        {
            var bounds = page.Bounds;
            var area = page.PrintableArea;
            var hundredths = Math.Max(
                Math.Max(area.X, area.Y),
                Math.Max(bounds.Width - area.Right, bounds.Height - area.Bottom));
            return Math.Clamp(hundredths, 0, 100) * HundredthsToPoints;
        }
        catch
        {
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    private static PrinterDriverSettings? PrinterPropertiesWindows(string printerName, IntPtr owner, byte[]? current)
    {
        var settings = new PrinterSettings { PrinterName = printerName };
        if (!settings.IsValid) return null;
        if (!NativeMethods.OpenPrinter(printerName, out var printer, IntPtr.Zero)) return null;

        var input = current is { Length: > 0 } ? ToHGlobal(current) : settings.GetHdevmode();
        try
        {
            var size = NativeMethods.DocumentProperties(owner, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) return null;

            var output = Marshal.AllocHGlobal(size);
            try
            {
                int result;
                var locked = NativeMethods.GlobalLock(input);
                try
                {
                    result = NativeMethods.DocumentProperties(owner, printer, printerName, output, locked,
                        NativeMethods.DmInBuffer | NativeMethods.DmInPrompt | NativeMethods.DmOutBuffer);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(input);
                }
                if (result != NativeMethods.IdOk) return null;

                var devMode = new byte[size];
                Marshal.Copy(output, devMode, 0, size);
                return ReadDriverSettings(printerName, devMode);
            }
            finally
            {
                Marshal.FreeHGlobal(output);
            }
        }
        finally
        {
            NativeMethods.GlobalFree(input);
            NativeMethods.ClosePrinter(printer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PrinterDriverSettings ReadDriverSettings(string printerName, byte[] devMode)
    {
        var settings = new PrinterSettings { PrinterName = printerName };
        var page = new PageSettings(settings);
        ApplyDevMode(settings, page, devMode);
        return new PrinterDriverSettings(
            devMode,
            settings.Copies,
            settings.Duplex switch
            {
                Duplex.Vertical => PrintDuplex.LongEdge,
                Duplex.Horizontal => PrintDuplex.ShortEdge,
                _ => PrintDuplex.Simplex,
            },
            page.Color,
            page.Landscape,
            page.PaperSource?.SourceName,
            page.PaperSize?.PaperName);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDevMode(PrinterSettings settings, PageSettings page, byte[] devMode)
    {
        var handle = ToHGlobal(devMode);
        try
        {
            settings.SetHdevmode(handle);
            page.SetHdevmode(handle);
        }
        finally
        {
            NativeMethods.GlobalFree(handle);
        }
    }

    private static IntPtr ToHGlobal(byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)data.Length);
        var pointer = NativeMethods.GlobalLock(handle);
        Marshal.Copy(data, 0, pointer, data.Length);
        NativeMethods.GlobalUnlock(handle);
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static string? DefaultPrinterWindows()
    {
        var name = new PrinterSettings().PrinterName;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> InstalledPrintersWindows()
    {
        var result = new List<string>();
        foreach (string? name in PrinterSettings.InstalledPrinters)
            if (!string.IsNullOrEmpty(name))
                result.Add(name);
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanDuplexWindows(string printerName)
    {
        try
        {
            return new PrinterSettings { PrinterName = printerName } is { IsValid: true, CanDuplex: true };
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static bool TryStart(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the most recent page raster as a GDI+ bitmap. Poster tiles draw the same page on every sheet,
    /// so this avoids re-rendering a large raster per tile; other layouts simply replace it as they go.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class PageImageCache : IDisposable
    {
        private (int Page, int Dpi) _key = (-1, 0);
        private Bitmap? _bitmap;
        private GCHandle _pin;

        public Bitmap Get(int page, int dpi, Func<int, int, RenderedPage> render)
        {
            if (_bitmap is not null && _key == (page, dpi)) return _bitmap;
            Dispose();

            var raster = render(page, dpi);
            // GDI+ reads the pixels in place for the bitmap's lifetime, so the buffer stays pinned until then.
            _pin = GCHandle.Alloc(raster.Bgra, GCHandleType.Pinned);
            _bitmap = new Bitmap(raster.Width, raster.Height, raster.Stride, PixelFormat.Format32bppArgb, _pin.AddrOfPinnedObject());
            _key = (page, dpi);
            return _bitmap;
        }

        public void Dispose()
        {
            _bitmap?.Dispose();
            _bitmap = null;
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    private static class NativeMethods
    {
        public const int DmOutBuffer = 2;
        public const int DmInPrompt = 4;
        public const int DmInBuffer = 8;
        public const int IdOk = 1;
        public const uint GmemMoveable = 0x0002;

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr printer);

        [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int DocumentProperties(IntPtr hwnd, IntPtr printer, string deviceName, IntPtr devModeOutput, IntPtr devModeInput, int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr handle);
    }
}
