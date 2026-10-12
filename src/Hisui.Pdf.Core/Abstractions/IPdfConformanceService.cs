using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Preflight checks and conversion for PDF versions and standards (PDF/A, PDF/UA, PDF/X).</summary>
public interface IPdfConformanceService
{
    Task<ConformanceReport> CheckAsync(byte[] pdf, PdfProfile profile, CancellationToken ct = default);

    /// <summary>Fixes what can be fixed for <paramref name="profile"/> and returns the result with the issues still left.</summary>
    Task<(byte[] Pdf, ConformanceReport Remaining)> ConvertAsync(byte[] pdf, PdfProfile profile, ConversionOptions options, CancellationToken ct = default);
}
