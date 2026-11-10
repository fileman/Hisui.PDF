namespace Hisui.Pdf.Core.Model;

/// <summary>Options for PDF → Word conversion.</summary>
public sealed record WordExportOptions
{
    /// <summary>When true (default) scanned pages are rebuilt from OCR text instead of their image strips.</summary>
    public bool UseOcr { get; init; } = true;

    /// <summary>OCR language / data settings; <see cref="OcrOptions.OnlyScannedPages"/> is always forced on.</summary>
    public OcrOptions? Ocr { get; init; }
}

/// <summary>The converted document plus what happened to scanned pages.</summary>
public sealed record WordExportResult(byte[] Docx, IReadOnlyList<int> OcrPages, string? OcrUnavailableReason);
