using System.Runtime.InteropServices;

namespace Hisui.Pdf.Core.Native;

/// <summary>
/// Minimal P/Invoke surface over the PDFium binary shipped by the <c>bblanchon.PDFium.*</c> packages
/// (the same <c>pdfium</c> library PDFtoImage loads). Only what <see cref="Services.PdfiumRenderer"/>
/// needs is declared. PDFium is not thread-safe: every call must happen under
/// <see cref="Services.PdfiumRenderer"/>'s process-wide gate.
/// </summary>
internal static partial class PdfiumNative
{
    private const string Lib = "pdfium";

    /// <summary><c>FPDF_ANNOT</c>: render annotations as well as page content.</summary>
    public const int FpdfAnnot = 0x01;

    /// <summary><c>FPDFBitmap_BGRA</c>: 4 bytes per pixel, byte order B, G, R, A.</summary>
    public const int BitmapBgra = 4;

    [LibraryImport(Lib)]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, nuint size, IntPtr password);

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(Lib)]
    public static partial nuint FPDF_GetLastError();

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [LibraryImport(Lib)]
    public static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageWidthF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [LibraryImport(Lib)]
    public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [LibraryImport(Lib)]
    public static partial void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [LibraryImport(Lib)]
    public static partial void FPDF_RemoveFormFieldHighlight(IntPtr formHandle);

    [LibraryImport(Lib)]
    public static partial void FPDF_FFLDraw(
        IntPtr formHandle, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
