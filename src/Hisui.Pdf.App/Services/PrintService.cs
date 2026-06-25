using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.Versioning;
using Hisui.Pdf.Core.Abstractions;

namespace Hisui.Pdf.App.Services;

/// <inheritdoc />
internal sealed class PrintService : IPrintService
{
    // Rasterisation DPI for the print path: high enough for crisp text/lines, low enough to stay fast and
    // within driver spool limits. The page image is then scaled to the printer's printable area.
    private const int PrintDpi = 200;

    private readonly IPdfRenderer _renderer;

    public PrintService(IPdfRenderer renderer) => _renderer = renderer;

    public bool SupportsSystemDialog => OperatingSystem.IsWindows();

    public string? DefaultPrinter => OperatingSystem.IsWindows() ? DefaultPrinterWindows() : null;

    public IReadOnlyList<string> GetPrinters() =>
        OperatingSystem.IsWindows() ? InstalledPrintersWindows() : [];

    public bool SupportsDuplex(string printerName) =>
        OperatingSystem.IsWindows() && !string.IsNullOrEmpty(printerName) && CanDuplexWindows(printerName);

    public Task<bool> PrintAsync(byte[] pdf, PrintJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(job);

        if (!OperatingSystem.IsWindows())
            return PrintViaShellAsync(pdf, ct); // no rich path off Windows

        if (job.Pages.Count == 0 || string.IsNullOrEmpty(job.PrinterName))
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            // Re-assert the platform inside the closure: the outer guard doesn't flow into the lambda,
            // which is what the windows-only PrintDocument API needs to see.
            if (!OperatingSystem.IsWindows()) return false;
            return PrintWindows(pdf, job, ct);
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
    private bool PrintWindows(byte[] pdf, PrintJob job, CancellationToken ct)
    {
        using var pd = new PrintDocument();
        pd.DocumentName = "Hisui PDF";
        pd.PrinterSettings.PrinterName = job.PrinterName;
        if (!pd.PrinterSettings.IsValid) return false;

        pd.PrinterSettings.Copies = (short)Math.Clamp(job.Copies, 1, 99);
        if (pd.PrinterSettings.CanDuplex)
            pd.PrinterSettings.Duplex = job.Duplex switch
            {
                PrintDuplex.LongEdge => Duplex.Vertical,
                PrintDuplex.ShortEdge => Duplex.Horizontal,
                _ => Duplex.Simplex,
            };

        var cursor = 0;
        pd.PrintPage += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            var pageIndex = job.Pages[cursor];

            var png = _renderer.RenderPageToPngAsync(pdf, pageIndex, PrintDpi, ct).GetAwaiter().GetResult();
            // GDI+ keeps a reference to the source stream for the image's lifetime, so the MemoryStream
            // must stay open until after DrawImage — keep both in scope and dispose together.
            using var stream = new MemoryStream(png);
            using var image = Image.FromStream(stream);

            // Fit the rendered page inside the printable area, preserving aspect ratio.
            var bounds = e.MarginBounds;
            var dest = FitPreserveAspect(image.Width, image.Height, bounds);
            e.Graphics!.DrawImage(image, dest);

            cursor++;
            e.HasMorePages = cursor < job.Pages.Count;
        };

        pd.Print();
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static Rectangle FitPreserveAspect(int imgW, int imgH, Rectangle bounds)
    {
        if (imgW <= 0 || imgH <= 0) return bounds;
        var scale = Math.Min((double)bounds.Width / imgW, (double)bounds.Height / imgH);
        var w = Math.Max(1, (int)Math.Round(imgW * scale));
        var h = Math.Max(1, (int)Math.Round(imgH * scale));
        var x = bounds.X + (bounds.Width - w) / 2;
        var y = bounds.Y + (bounds.Height - h) / 2;
        return new Rectangle(x, y, w, h);
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
}
