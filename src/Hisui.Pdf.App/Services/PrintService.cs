using System.Diagnostics;
using System.IO;

namespace Hisui.Pdf.App.Services;

/// <inheritdoc />
internal sealed class PrintService : IPrintService
{
    public async Task<bool> PrintAsync(byte[] pdf, CancellationToken ct = default)
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
