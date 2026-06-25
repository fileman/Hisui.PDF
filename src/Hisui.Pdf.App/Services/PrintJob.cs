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
/// A fully resolved print request: which printer, how many copies, duplex mode, and the exact ordered
/// list of zero-based page indices to print. Produced by the print dialog and consumed by the print service.
/// </summary>
public sealed record PrintJob
{
    public required string PrinterName { get; init; }
    public int Copies { get; init; } = 1;
    public PrintDuplex Duplex { get; init; } = PrintDuplex.Simplex;

    /// <summary>Zero-based page indices to print, in order. Empty means nothing to print.</summary>
    public required IReadOnlyList<int> Pages { get; init; }
}
