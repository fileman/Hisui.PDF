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

    /// <summary>
    /// Re-expresses this rect after the page it lives on is rotated clockwise by <paramref name="degrees"/>
    /// (0/90/180/270; 360 - n undoes n). Normalised top-left coordinates stay normalised.
    /// </summary>
    public PdfRect RotateClockwise(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90  => new PdfRect(1 - Bottom, Left, 1 - Top, Right),
        180 => new PdfRect(1 - Right, 1 - Bottom, 1 - Left, 1 - Top),
        270 => new PdfRect(Top, 1 - Right, Bottom, 1 - Left),
        _   => this,
    };

    /// <summary>Creates a rect from a top-left corner and explicit size.</summary>
    public static PdfRect FromLTWH(double left, double top, double width, double height)
        => new(left, top, left + width, top + height);
}
