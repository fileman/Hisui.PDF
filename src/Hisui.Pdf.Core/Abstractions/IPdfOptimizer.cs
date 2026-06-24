namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Best-effort PDF size reduction. Rewrites content streams with zlib compression and removes
/// cross-reference table bloat left by incremental saves. Preserves all document structure.
/// </summary>
public interface IPdfOptimizer
{
    Task<byte[]> OptimizeAsync(byte[] pdf, CancellationToken ct = default);
}
