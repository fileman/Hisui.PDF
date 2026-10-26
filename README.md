# Hisui PDF

A cross-platform desktop PDF editor built on a fully free/MIT-licensed stack.

Hisui PDF lets you reorganise, annotate, redact and protect PDF documents from a
single Avalonia desktop app that runs on Windows, Linux and macOS.

## Download

### winget (Windows)

```powershell
winget install Fileman.HisuiPDF
```

Upgrade later with `winget upgrade Fileman.HisuiPDF`.

### Release assets

Grab a ready-to-run build from the
[**Releases**](https://github.com/fileman/Hisui.PDF/releases/latest) page — the
binaries are self-contained, so **no .NET install is required**.

| Platform | Asset |
|---|---|
| Windows x64 (installer) | `HisuiPDF-Setup-X.Y.N-win-x64.exe` (asks for UAC up front; works where AppLocker blocks unsigned MSIs) or `HisuiPDF-X.Y.N-win-x64.msi` |
| Windows x64 (portable) | `Hisui.Pdf-vX.Y.N-win-x64.zip` |
| Linux x64 | `Hisui.Pdf-vX.Y.N-linux-x64.tar.gz` |
| macOS (Intel) | `Hisui.Pdf-vX.Y.N-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `Hisui.Pdf-vX.Y.N-osx-arm64.tar.gz` |

The release tag is `vX.Y.N`: `X.Y` is chosen when releasing, `N` is the commit count of the released build.

On Windows run the MSI (it is unsigned, so SmartScreen/UAC will warn), or unpack
the zip. On Linux/macOS unpack the archive and run `Hisui.Pdf.App`. On macOS the
binary is unsigned, so on first launch right-click → *Open* (or run
`xattr -dr com.apple.quarantine Hisui.Pdf.App`).

Releases started with the *prerelease* option are published as pre-releases.

### Updates

Hisui PDF checks GitHub for a newer stable release at startup (disable with `"CheckForUpdates": false` in
`%APPDATA%\Hisui.PDF\settings.json`) and from *? Help* → *Check for updates…*. On Windows, accepting
downloads the MSI and runs it as an in-place upgrade; on other platforms it opens
the release page. Pre-releases are never offered.

## Features

- **Pages** — merge, split, extract, reorder, rotate and delete pages, with
  non-destructive editing and full undo/redo. Multiple documents in tabs, with
  an unsaved-changes prompt on close.
- **Annotations** — highlight, rectangle, free text, sticky notes, image
  overlays, editable watermarks and header/footer with page, date and Bates
  tokens (all undoable). Free text is typed straight onto the page, with
  optional character spacing for boxed forms (IBAN, tax codes); every
  annotation can be moved and resized before it is applied.
- **Comments, bookmarks & links** — real PDF comments with replies and a
  comments panel; bookmarks panel (add, nest, rename, reorder); link tool with
  clickable links. Saving keeps outline, links, tags and metadata.
- **Reusable signatures** — build a library of signatures (draw freehand, import
  a PNG, or type cursive text) and reuse them across documents.
- **Digital signatures (experimental)** — PAdES signing with a PFX certificate,
  appended as an incremental update so existing signatures stay valid; sign
  into empty signature fields or invisibly; verify signatures.
- **Forms** — fill AcroForm fields in place (formats, required/invalid
  highlight); automatic field detection; layout mode with multi-select, align,
  resize, tab order and field properties; import/export data (XFDF, FDF, CSV),
  reset and flatten.
- **Text editing** — replace a word in place (redact-and-retype).
- **Redaction** — secure redaction that rasterises the page and removes the
  underlying text/vector content; find-and-redact by pattern (tax code, IBAN,
  VAT, e-mail, phone, date, custom regex).
- **Compare** — text comparison with another PDF, with marks and a report.
- **Standards** — preflight and conversion to PDF 1.4–2.0, PDF/A-1b/2b/2u/3b/3u,
  PDF/UA-1, PDF/X-4, plus an accessibility check.
- **Security & metadata** — AES-256 encryption, password removal, document
  properties.
- **Print & compress** — print with preview and options (printer, copies,
  duplex, page range); reduce file size by recompressing images.
- **Export & extraction** — convert to Word (.docx), Excel (.xlsx) and
  PowerPoint (.pptx); pull out text (with coordinates) and embedded images.
- **OCR** — turn scanned (image-only) PDFs into searchable PDFs by adding an
  invisible, selectable text layer (Tesseract, parallel across pages). Scanned
  pages are detected automatically; extra languages can be downloaded on demand.
- **Batch processing** — apply one operation (OCR, compression, PDF/A, Word,
  Excel, PowerPoint, text) to many PDFs; originals are never changed.
- **Recent files** — start page with a table of recent documents (thumbnail,
  folder, last opened, size) and pinning.
- **Localization** — English and Italian UI, switchable at runtime; light, dark
  or system theme (light theme meets WCAG AA contrast).
- **Help & diagnostics** — the *? Help* button reports a problem or requests a
  feature on GitHub, checks for updates and opens *View log…* (warnings, errors
  and stack traces from `%APPDATA%\Hisui.PDF\hisui.log`).

## Tech stack

- **.NET 10** / C#
- **[Avalonia](https://avaloniaui.net/) 12** — cross-platform UI
- **[PDFsharp](https://docs.pdfsharp.net/)** — structural & write operations
- **[PdfPig](https://github.com/UglyToad/PdfPig)** — text/image extraction
- **[Open XML SDK](https://github.com/dotnet/Open-XML-SDK)** — Word (.docx) export
- **[PDFtoImage](https://github.com/sungaila/PDFtoImage)** (PDFium) — page rendering
- **[Tesseract](https://github.com/charlesw/tesseract)** — OCR for scanned PDFs
- **[SkiaSharp](https://github.com/mono/SkiaSharp)** — raster compositing
- **CommunityToolkit.Mvvm** + **Microsoft.Extensions.Hosting** — MVVM & DI

## Project structure

| Project | Target | Role |
|---|---|---|
| `src/Hisui.Pdf.Core` | `net10.0` | PDF engine, no UI dependencies |
| `src/Hisui.Pdf.App` | `net10.0` | Avalonia desktop app (MVVM) |
| `src/Hisui.Pdf.Installer.Win` | WiX 4 | Windows MSI installer |
| `tests/Hisui.Pdf.Core.Tests` | `net10.0` | xUnit v3 tests |

## Build & run

```bash
# Build
dotnet build Hisui.Pdf.slnx

# Run the app
dotnet run --project src/Hisui.Pdf.App

# Run the tests (xUnit v3 on Microsoft Testing Platform — run the test exe)
dotnet test Hisui.Pdf.slnx
```

### Packaging

Installer/package scripts live in `build/`: `package-msi.ps1` (signed MSI via
WiX), `package-win.ps1`, `package-linux.sh`, `package-osx.sh`. Releases are built
by the GitHub Actions *Release* workflow, started manually from the Actions tab
(or `gh workflow run release.yml -f version=0.1`). It creates the `vX.Y.N` tag itself.

### OCR setup

OCR works out of the box. The **English and Italian** language data
(`eng`/`ita`, the `tessdata_fast` models) ships in a `tessdata` folder next to
the executable, and the native engine binaries are bundled on Windows via the
`Tesseract` package.

- **More languages** — main menu → *Preferences* → *OCR languages…* downloads
  other languages on demand from
  [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) or imports a
  `.traineddata` file offline. You can also drop `*.traineddata` files into the
  `tessdata` folder next to the app (or point `TESSDATA_PREFIX` at them).
- **Native libraries on Linux/macOS** — install the system Tesseract/Leptonica:
  on Linux `libtesseract`/`libleptonica`, on macOS `brew install tesseract leptonica`.

If a language or native library is missing the app reports a clear message
instead of failing — the rest of the editor keeps working.

## License

Hisui PDF is released under the [MIT License](LICENSE).

It bundles third-party components under permissive licenses (MIT, Apache-2.0,
BSD-3-Clause) and the Inter font (SIL OFL 1.1). See
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the full attributions.
