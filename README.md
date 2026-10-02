# Hisui PDF

A cross-platform desktop PDF editor built on a fully free/MIT-licensed stack.

Hisui PDF lets you reorganise, annotate, redact and protect PDF documents from a
single Avalonia desktop app that runs on Windows, Linux and macOS.

## Download

Grab a ready-to-run build from the
[**Releases**](https://github.com/fileman/Hisui.PDF/releases/latest) page — the
binaries are self-contained, so **no .NET install is required**.

| Platform | Asset |
|---|---|
| Windows x64 (installer) | `HisuiPDF-X.Y.Z-win-x64.msi` |
| Windows x64 (portable) | `Hisui.Pdf-vX.Y.Z-win-x64.zip` |
| Linux x64 | `Hisui.Pdf-vX.Y.Z-linux-x64.tar.gz` |
| macOS (Intel) | `Hisui.Pdf-vX.Y.Z-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `Hisui.Pdf-vX.Y.Z-osx-arm64.tar.gz` |

On Windows run the MSI (once SignPath signing is active it is signed; unsigned builds make SmartScreen/UAC warn), or unpack
the zip. On Linux/macOS unpack the archive and run `Hisui.Pdf.App`. On macOS the
binary is unsigned, so on first launch right-click → *Open* (or run
`xattr -dr com.apple.quarantine Hisui.Pdf.App`).

Releases whose tag has a suffix (e.g. `v0.1.0-beta4`) are published as
pre-releases.

### Updates

Hisui PDF checks GitHub for a newer stable release at startup (disable with `"CheckForUpdates": false` in
`%APPDATA%\Hisui.PDF\settings.json`) and from the main menu → *Check for updates…*. On Windows, accepting
downloads the MSI and runs it as an in-place upgrade; on other platforms it opens
the release page. Pre-releases are never offered.

## Features

- **Pages** — merge, split, extract, reorder, rotate and delete pages, with
  non-destructive editing and full undo/redo.
- **Annotations** — highlight, rectangle, free text, sticky notes, image
  overlays and document-wide watermarks (all undoable).
- **Reusable signatures** — build a library of signatures (draw freehand, import
  a PNG, or type cursive text) and reuse them across documents.
- **Text editing** — replace a word in place (redact-and-retype).
- **Redaction** — secure redaction that rasterises the page and removes the
  underlying text/vector content.
- **Forms** — read and fill AcroForm fields.
- **Security & metadata** — AES-256 encryption, password removal, document
  properties.
- **Print & compress** — print with preview and options (printer, copies,
  duplex, page range); reduce file size by recompressing images.
- **Extraction** — pull out text (with coordinates) and embedded images.
- **OCR** — turn scanned (image-only) PDFs into searchable PDFs by adding an
  invisible, selectable text layer (Tesseract). Scanned pages are detected
  automatically.
- **Localization** — English and Italian UI, switchable at runtime; light, dark
  or system theme.
- **Diagnostics** — main menu → *View log…* shows warnings, errors and stack
  traces from `%APPDATA%\Hisui.PDF\hisui.log`; unhandled exceptions are logged.

## Tech stack

- **.NET 10** / C#
- **[Avalonia](https://avaloniaui.net/) 12** — cross-platform UI
- **[PDFsharp](https://docs.pdfsharp.net/)** — structural & write operations
- **[PdfPig](https://github.com/UglyToad/PdfPig)** — text/image extraction
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
by the GitHub Actions *Release* workflow when a `v*` tag is pushed:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

### OCR setup

OCR works out of the box. The **English and Italian** language data
(`eng`/`ita`, the `tessdata_fast` models) ships in a `tessdata` folder next to
the executable, and the native engine binaries are bundled on Windows via the
`Tesseract` package.

- **More languages** — drop extra `*.traineddata` files into the `tessdata`
  folder next to the app (or point the `TESSDATA_PREFIX` environment variable at
  them) and pass the language code. Grab them from
  [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast).
- **Native libraries on Linux/macOS** — install the system Tesseract/Leptonica:
  on Linux `libtesseract`/`libleptonica`, on macOS `brew install tesseract leptonica`.

If a language or native library is missing the app reports a clear message
instead of failing — the rest of the editor keeps working.

## License

Hisui PDF is released under the [MIT License](LICENSE).

It bundles third-party components under permissive licenses (MIT, Apache-2.0,
BSD-3-Clause) and the Inter font (SIL OFL 1.1). See
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the full attributions.

### Code signing policy

Windows installers are signed with a certificate provided by
[SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org). Signing happens only in the GitHub
Actions *Release* workflow, on builds from this repository's `v*` tags.
This program does not transmit any information to other networked systems
except the update check against GitHub Releases.
