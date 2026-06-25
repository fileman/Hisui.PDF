namespace Hisui.Pdf.App.Services;

/// <summary>
/// Printing. On Windows the service drives the print stack directly (printer enumeration, duplex,
/// per-page rendering) so the app can offer an in-app preview + options dialog. Elsewhere — where there
/// is no cross-platform print API — it falls back to handing the file to the OS default viewer/printer.
/// </summary>
public interface IPrintService
{
    /// <summary>True when the rich in-app print dialog (printer list, duplex, page range) is available.</summary>
    bool SupportsSystemDialog { get; }

    /// <summary>The system default printer name, or null if none / not on a supported platform.</summary>
    string? DefaultPrinter { get; }

    /// <summary>Installed printer names. Empty when no rich print stack is available.</summary>
    IReadOnlyList<string> GetPrinters();

    /// <summary>True if the named printer reports duplex (double-sided) capability.</summary>
    bool SupportsDuplex(string printerName);

    /// <summary>Prints <paramref name="pdf"/> with the resolved <paramref name="job"/>. Returns true on success.</summary>
    Task<bool> PrintAsync(byte[] pdf, PrintJob job, CancellationToken ct = default);

    /// <summary>
    /// Fallback path: writes the PDF to a temp file and hands it to the OS (Windows shell "print" verb,
    /// or the default viewer on macOS / Linux). Returns true if the action was launched.
    /// </summary>
    Task<bool> PrintViaShellAsync(byte[] pdf, CancellationToken ct = default);
}
