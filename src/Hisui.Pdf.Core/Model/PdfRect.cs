namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A rectangle in normalized page coordinates where (0,0) is the top-left corner of the page
/// and (1,1) is the bottom-right corner. Y increases downward (screen convention), matching the
/// coordinate space of rendered page images returned by <see cref="Abstractions.IPdfRenderer"/>.
/// </summary>
public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    /// <summary>Creates a rect from a top-left corner and explicit size.</summary>
    public static PdfRect FromLTWH(double left, double top, double width, double height)
        => new(left, top, left + width, top + height);
}
