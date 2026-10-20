using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Printing;

namespace Hisui.Pdf.App.Services;

/// <summary>Double-sided printing mode requested by the user (mapped to the driver on Windows).</summary>
public enum PrintDuplex
{
    /// <summary>Single-sided.</summary>
    Simplex,

    /// <summary>Double-sided, flipped on the long edge (book / portrait binding).</summary>
    LongEdge,

    /// <summary>Double-sided, flipped on the short edge (notepad / landscape binding).</summary>
    ShortEdge,
}

/// <summary>
/// A fully resolved print request: which printer, how many copies, duplex mode, colour handling, and the
/// exact sheets to print (already laid out by <see cref="PrintLayoutEngine"/>, so the printed output is
/// what the dialog previewed). Produced by the print dialog and consumed by the print service.
/// </summary>
public sealed record PrintJob
{
    public required string PrinterName { get; init; }
    public int Copies { get; init; } = 1;
    public PrintDuplex Duplex { get; init; } = PrintDuplex.Simplex;
    public PdfRenderColorMode ColorMode { get; init; } = PdfRenderColorMode.Color;

    /// <summary>Paper source (tray) name as the driver reports it; null lets the printer choose.</summary>
    public string? PaperSource { get; init; }

    /// <summary>Sheets to print, in order. Empty means nothing to print.</summary>
    public required IReadOnlyList<PrintSheet> Sheets { get; init; }

    /// <summary>
    /// Driver settings (a Windows DEVMODE) captured from the printer's own properties dialog, applied
    /// before the options above so driver-only features (stapling, quality, …) carry through. Null for none.
    /// </summary>
    public byte[]? DriverSettings { get; init; }
}

/// <summary>A paper size a printer offers, with its unprintable border.</summary>
public sealed record PrinterPaper(string Name, double WidthPoints, double HeightPoints, double MarginPoints)
{
    public PrintPaper ToLayoutPaper() => new(Name, WidthPoints, HeightPoints, MarginPoints);
}

/// <summary>What a printer can do, as far as the print dialog needs to know.</summary>
public sealed record PrinterCapabilities(
    bool CanDuplex,
    bool SupportsColor,
    IReadOnlyList<string> PaperSources,
    IReadOnlyList<PrinterPaper> PaperSizes,
    int DefaultPaperIndex)
{
    public static PrinterCapabilities Fallback { get; } = new(false, true, [], [new PrinterPaper("A4", 595.276, 841.89, 0)], 0);
}

/// <summary>Settings read back from the printer's properties dialog, to refresh the in-app controls.</summary>
public sealed record PrinterDriverSettings(
    byte[] DevMode,
    int Copies,
    PrintDuplex Duplex,
    bool Color,
    bool Landscape,
    string? PaperSource,
    string? PaperSize);
