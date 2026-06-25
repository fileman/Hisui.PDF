namespace Hisui.Pdf.App.Services;

/// <summary>
/// Sends a PDF to the platform's print path. Avalonia has no cross-platform print API, so the document
/// is written to a temp file and handed to the OS: the Windows shell "print" verb, or the default
/// viewer on macOS / Linux (where the user prints from its native print dialog).
/// </summary>
public interface IPrintService
{
    /// <summary>Returns true if the print/open action was launched.</summary>
    Task<bool> PrintAsync(byte[] pdf, CancellationToken ct = default);
}
