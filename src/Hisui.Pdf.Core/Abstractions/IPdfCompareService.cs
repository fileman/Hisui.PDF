using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Text comparison of two versions of a document.</summary>
public interface IPdfCompareService
{
    /// <summary>Differences of <paramref name="revised"/> against <paramref name="original"/>: marks on the original's pages and a per-page summary.</summary>
    Task<DocumentComparison> CompareAsync(byte[] original, byte[] revised, CancellationToken ct = default);
}
