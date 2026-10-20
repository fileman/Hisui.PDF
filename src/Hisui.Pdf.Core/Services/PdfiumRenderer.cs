using System.Runtime.InteropServices;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Native;
using SkiaSharp;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// <see cref="IPdfRenderer"/> backed by PDFium. Opened documents are kept in a small LRU cache keyed by
/// the <c>byte[]</c> reference (session sources are immutable arrays that are swapped, never edited in
/// place), so a document is parsed once and each page render only pays for rasterisation.
/// </summary>
/// <remarks>
/// PDFium is not thread-safe, so every native call in the process runs under <see cref="Gate"/>.
/// Rendering is CPU-bound and runs on the thread pool to keep the UI thread free. Other Core services (text
/// extraction) reach PDFium only through <see cref="WithDocument{T}"/>, which holds the same gate. Nothing else in Core
/// may call PDFium (or PDFtoImage, which wraps the same library) outside this class.
/// </remarks>
internal sealed class PdfiumRenderer : IPdfRenderer, IPdfiumDocumentHost, IDisposable
{
    /// <summary>Maximum number of simultaneously open documents; the least recently used is closed first.</summary>
    internal const int MaxOpenDocuments = 8;

    private const uint White = 0xFFFFFFFF;

    // PDFium keeps process-global state, so the gate and library initialisation are static even though
    // the document cache is per instance.
    private static readonly object Gate = new();
    private static bool _libraryInitialized;

    // Guarded by Gate. Most recently used at the front.
    private readonly LinkedList<PdfiumDocument> _lru = new();
    private readonly Dictionary<byte[], LinkedListNode<PdfiumDocument>> _byBuffer = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    /// <summary>Number of documents currently open in this renderer (diagnostics and tests).</summary>
    internal int OpenDocumentCount
    {
        get { lock (Gate) return _lru.Count; }
    }

    public Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Gate) return GetDocument(pdf).PageCount;
            },
            cancellationToken);
    }

    public async Task<byte[]> RenderPageToPngAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default)
    {
        var page = await RenderPageAsync(pdf, pageIndex, dpi, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => EncodePng(page), cancellationToken).ConfigureAwait(false);
    }

    public Task<RenderedPage> RenderPageAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default) =>
        RenderPageAsync(pdf, pageIndex, dpi, PdfRenderOptions.Default, cancellationToken);

    public Task<IReadOnlyList<PdfPageSize>> GetPageSizesAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<PdfPageSize>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Gate) return GetDocument(pdf).PageSizes();
            },
            cancellationToken);
    }

    public Task<RenderedPage> RenderPageAsync(byte[] pdf, int pageIndex, int dpi, PdfRenderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (Gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return GetDocument(pdf).Render(pageIndex, dpi, options);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public T WithDocument<T>(byte[] pdf, Func<IntPtr, int, T> work)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(work);
        lock (Gate)
        {
            var doc = GetDocument(pdf);
            return work(doc.Handle, doc.PageCount);
        }
    }

    public void Release(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        lock (Gate)
        {
            if (!_byBuffer.Remove(pdf, out var node)) return;
            _lru.Remove(node);
            node.Value.Dispose();
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            _disposed = true;
            foreach (var doc in _lru) doc.Dispose();
            _lru.Clear();
            _byBuffer.Clear();
        }
    }

    /// <summary>Returns the cached document for <paramref name="pdf"/>, opening (and evicting) as needed. Caller holds <see cref="Gate"/>.</summary>
    private PdfiumDocument GetDocument(byte[] pdf)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_byBuffer.TryGetValue(pdf, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value;
        }

        if (!_libraryInitialized)
        {
            PdfiumNative.FPDF_InitLibrary();
            _libraryInitialized = true;
        }

        var doc = PdfiumDocument.Open(pdf);
        _byBuffer[pdf] = _lru.AddFirst(doc);

        while (_lru.Count > MaxOpenDocuments)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _byBuffer.Remove(last.Value.Buffer);
            last.Value.Dispose();
        }
        return doc;
    }

    internal static byte[] EncodePng(RenderedPage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, page.Bgra, page.Stride);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// A PDF held open in PDFium. The source array stays pinned for the document's lifetime because
    /// PDFium reads from the buffer lazily rather than copying it.
    /// </summary>
    private sealed class PdfiumDocument : IDisposable
    {
        // FPDF_FORMFILLINFO is a large struct of optional callbacks; version 1 plus all-null callbacks
        // is a valid "no host integration" environment. Oversized and zeroed so layout drift is harmless.
        private const int FormFillInfoSize = 512;

        private readonly GCHandle _pin;
        private IntPtr _document;
        private IntPtr _formInfo;
        private IntPtr _form;

        private PdfiumDocument(byte[] buffer, GCHandle pin, IntPtr document, int pageCount)
        {
            Buffer = buffer;
            _pin = pin;
            _document = document;
            PageCount = pageCount;
        }

        public byte[] Buffer { get; }

        public int PageCount { get; }

        public IntPtr Handle => _document;

        public static PdfiumDocument Open(byte[] buffer)
        {
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var document = PdfiumNative.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)buffer.Length, IntPtr.Zero);
            if (document == IntPtr.Zero)
            {
                var error = PdfiumNative.FPDF_GetLastError();
                pin.Free();
                throw new InvalidOperationException($"PDFium could not open the document ({DescribeError(error)}).");
            }

            var result = new PdfiumDocument(buffer, pin, document, PdfiumNative.FPDF_GetPageCount(document));
            result.InitFormFill();
            return result;
        }

        /// <summary>
        /// Sets up the form-fill environment so AcroForm widgets are drawn (as PDFtoImage's
        /// <c>WithFormFill</c> did). Failure is non-fatal: pages still render, minus field widgets.
        /// </summary>
        private void InitFormFill()
        {
            _formInfo = Marshal.AllocHGlobal(FormFillInfoSize);
            unsafe { new Span<byte>((void*)_formInfo, FormFillInfoSize).Clear(); }
            Marshal.WriteInt32(_formInfo, 1); // FPDF_FORMFILLINFO.version
            _form = PdfiumNative.FPDFDOC_InitFormFillEnvironment(_document, _formInfo);
            if (_form != IntPtr.Zero) PdfiumNative.FPDF_RemoveFormFieldHighlight(_form);
        }

        public PdfPageSize[] PageSizes()
        {
            var sizes = new PdfPageSize[PageCount];
            for (var i = 0; i < sizes.Length; i++)
            {
                // Falls back to A4 for a page whose box can't be read, so layout never divides by zero.
                sizes[i] = PdfiumNative.FPDF_GetPageSizeByIndexF(_document, i, out var size) != 0 && size.Width > 0 && size.Height > 0
                    ? new PdfPageSize(size.Width, size.Height)
                    : new PdfPageSize(595.276, 841.89);
            }
            return sizes;
        }

        public RenderedPage Render(int pageIndex, int dpi, PdfRenderOptions options)
        {
            if ((uint)pageIndex >= (uint)PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page index {pageIndex} is out of range (0..{PageCount - 1}).");

            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero)
                throw new InvalidOperationException($"PDFium could not load page {pageIndex} ({DescribeError(PdfiumNative.FPDF_GetLastError())}).");

            try
            {
                // Page size is in points and already honours /Rotate. The point size is rounded to whole
                // points and then scaled (truncating) to the requested DPI, which reproduces the pixel
                // dimensions PDFtoImage used, so switching renderers never shifts layout by a pixel.
                var width = ScaleToPixels(PdfiumNative.FPDF_GetPageWidthF(page), dpi);
                var height = ScaleToPixels(PdfiumNative.FPDF_GetPageHeightF(page), dpi);
                var stride = checked(width * 4);
                var length = (long)stride * height;
                if (length > Array.MaxLength)
                    throw new InvalidOperationException($"Page {pageIndex} at {dpi} DPI ({width}x{height}) is too large to render.");

                // PDFium draws straight into this (pinned) array, so there is no copy-out step.
                var pixels = GC.AllocateUninitializedArray<byte>((int)length, pinned: true);
                var bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                    width, height, PdfiumNative.BitmapBgra, Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), stride);
                if (bitmap == IntPtr.Zero)
                    throw new InvalidOperationException("PDFium could not initialize the rendering bitmap.");

                var flags = PdfiumNative.FpdfAnnot;
                if (options.ColorMode == PdfRenderColorMode.Grayscale) flags |= PdfiumNative.FpdfGrayscale;
                if (options.ForPrinting) flags |= PdfiumNative.FpdfPrinting;

                // Recolouring edits only this transient page object tree (no content is regenerated), and
                // the page is closed below, so the document itself never changes.
                if (options.ColorMode == PdfRenderColorMode.BlackText)
                    BlackenText(page, PdfiumNative.FPDFPage_CountObjects(page), static (owner, i) => PdfiumNative.FPDFPage_GetObject(owner, i));

                try
                {
                    PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, White);
                    PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, flags);
                    if (_form != IntPtr.Zero)
                        PdfiumNative.FPDF_FFLDraw(_form, bitmap, page, 0, 0, width, height, 0, flags);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }

                return new RenderedPage(width, height, stride, pixels);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        public void Dispose()
        {
            if (_document == IntPtr.Zero) return;

            if (_form != IntPtr.Zero)
            {
                PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_form);
                _form = IntPtr.Zero;
            }
            PdfiumNative.FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
            if (_formInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_formInfo);
                _formInfo = IntPtr.Zero;
            }
            _pin.Free();
        }

        /// <summary>
        /// Sets the fill and stroke of every text object to black (keeping its opacity), descending into
        /// form XObjects, where headers and footers often live.
        /// </summary>
        private static void BlackenText(IntPtr owner, int count, Func<IntPtr, int, IntPtr> getObject)
        {
            for (var i = 0; i < count; i++)
            {
                var obj = getObject(owner, i);
                if (obj == IntPtr.Zero) continue;

                switch (PdfiumNative.FPDFPageObj_GetType(obj))
                {
                    case PdfiumNative.PageObjText:
                        var fillAlpha = PdfiumNative.FPDFPageObj_GetFillColor(obj, out _, out _, out _, out var fa) != 0 ? fa : 255;
                        var strokeAlpha = PdfiumNative.FPDFPageObj_GetStrokeColor(obj, out _, out _, out _, out var sa) != 0 ? sa : 255;
                        PdfiumNative.FPDFPageObj_SetFillColor(obj, 0, 0, 0, fillAlpha);
                        PdfiumNative.FPDFPageObj_SetStrokeColor(obj, 0, 0, 0, strokeAlpha);
                        break;
                    case PdfiumNative.PageObjForm:
                        BlackenText(obj, PdfiumNative.FPDFFormObj_CountObjects(obj),
                            static (form, j) => PdfiumNative.FPDFFormObj_GetObject(form, (uint)j));
                        break;
                }
            }
        }

        private static int ScaleToPixels(float points, int dpi) =>
            Math.Max(1, (int)(Math.Round((double)points) * dpi / 72.0));

        private static string DescribeError(nuint code) => code switch
        {
            1 => "unknown error",
            2 => "file not found or could not be opened",
            3 => "file not in PDF format or corrupted",
            4 => "password required or incorrect password",
            5 => "unsupported security scheme",
            6 => "page not found or content error",
            _ => $"error {code}",
        };
    }
}

/// <summary>
/// Lets other Core services run native PDFium work against the renderer's already-open document
/// for a buffer, under the process-wide PDFium gate.
/// </summary>
internal interface IPdfiumDocumentHost
{
    /// <summary>
    /// Runs <paramref name="work"/> with the raw <c>FPDF_DOCUMENT</c> handle and page count of
    /// <paramref name="pdf"/> (opened on demand and cached like a render would). The gate is held for
    /// the duration, so keep it short, and neither keep the handle nor close it after returning.
    /// </summary>
    T WithDocument<T>(byte[] pdf, Func<IntPtr, int, T> work);
}
