using System.Runtime.InteropServices;

namespace Hisui.Pdf.Core.Native;

/// <summary>
/// Minimal P/Invoke surface over the PDFium binary shipped by the <c>bblanchon.PDFium.*</c> packages
/// (the same <c>pdfium</c> library PDFtoImage loads). Only what <see cref="Services.PdfiumRenderer"/>
/// and <see cref="Services.PdfTextExtractor"/> need is declared. PDFium is not thread-safe: every call must happen under
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

    // ── text API (fpdf_text.h) ───────────────────────────────────────────────

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountChars(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);

    [LibraryImport(Lib)]
    public static partial double FPDFText_GetFontSize(IntPtr textPage, int index);

    /// <summary>
    /// "Loose" glyph box: the glyph's advance by the font's ascent-to-descent, in unrotated page space, so it has
    /// the same height for every glyph of a font at a given size. Returns 0 on failure.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out FsRectF rect);

    /// <summary>The glyph's fill colour, 0–255 per channel. Returns 0 on failure.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFText_GetFillColor(IntPtr textPage, int index, out uint r, out uint g, out uint b, out uint a);

    /// <summary>The glyph's font weight (400 normal, 700 bold), or -1 on failure.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFText_GetFontWeight(IntPtr textPage, int index);

    /// <summary>
    /// The glyph's font name (UTF-8, NUL-terminated) into <paramref name="buffer"/>, and its descriptor flags.
    /// Returns the buffer length needed; 0 on failure.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial uint FPDFText_GetFontInfo(IntPtr textPage, int index, Span<byte> buffer, uint length, out int flags);

    /// <summary>FS_RECTF: left, top, right, bottom (page space, y up).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FsRectF
    {
        public float Left, Top, Right, Bottom;
    }

    /// <summary>Glyph box in unrotated page space (origin bottom-left, y up). Returns 0 on failure.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    /// <summary>Rotation of the glyph's text matrix in radians, counter-clockwise in page space; -1 on failure.</summary>
    [LibraryImport(Lib)]
    public static partial float FPDFText_GetCharAngle(IntPtr textPage, int index);

    /// <summary>Copies up to <paramref name="count"/> UTF-16 units (plus a terminating NUL) into <paramref name="result"/>; returns units written including the NUL.</summary>
    [LibraryImport(Lib)]
    public static unsafe partial int FPDFText_GetText(IntPtr textPage, int startIndex, int count, char* result);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetRotation(IntPtr page);

    /// <summary>Maps page-space coordinates to device space (integers, hence the caller-chosen large size for precision).</summary>
    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(
        IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
}
