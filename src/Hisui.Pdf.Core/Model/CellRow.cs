namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A row of equal boxes meant for one character each (IBAN, codice fiscale). <see cref="Bounds"/> is normalised,
/// in rendered orientation; the pitch is the distance between consecutive box left edges, in points.
/// </summary>
public sealed record CellRow(PdfRect Bounds, int CellCount, double CellWidthPoints, double CellHeightPoints);
