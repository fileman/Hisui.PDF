namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Thrown when the OCR engine cannot run: the native Tesseract/Leptonica libraries are missing,
/// or the requested language's <c>traineddata</c> file cannot be found. The message is safe to show
/// to the user and explains how to resolve it.
/// </summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message) : base(message) { }
    public OcrUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
