# Hisui PDF — Handoff

Standalone cross-platform (Windows / Linux / macOS) PDF editor. **Avalonia 11.2 + .NET 10**, free/MIT stack:
PDFsharp 6.2.4 (write/burn-in), PdfPig 0.1.14 (read-only text words), PDFtoImage 5.2.1 + SkiaSharp/PDFium
(rendering), CommunityToolkit.Mvvm. Clean layering: `Hisui.Pdf.Core` (engine) ← `Hisui.Pdf.App` (Avalonia UI).

## Build / test / run

```powershell
# Build (0 errors; 2 cosmetic AVLN3001 warnings are expected — see Known constraints)
dotnet build Hisui.Pdf.slnx

# Tests — xUnit v3 on Microsoft Testing Platform. `dotnet test --filter` is IGNORED on MTP;
# run the test EXE directly:
& "tests\Hisui.Pdf.Core.Tests\bin\Debug\net10.0\Hisui.Pdf.Core.Tests.exe"     # expect 66/66

# Run the app
dotnet run --project src\Hisui.Pdf.App
```

Publish profiles exist for `win-x64`, `linux-x64`, `osx-x64` (Linux/macOS need a Liberation/DejaVu font installed;
macOS Apple Silicon → use `osx-arm64` RID).

## What this session delivered

### 1. WYSIWYG annotation editor + text editing
- **Core**: [AnnotationTool](src/Hisui.Pdf.Core/Model/AnnotationTool.cs), [AnnotationInputModel](src/Hisui.Pdf.Core/Model/AnnotationInputModel.cs),
  `TextWord.FontSizePoints`, `PdfDocumentSession.UpdateSource`, and **redact+retype** text editing via
  [IPdfTextEditService](src/Hisui.Pdf.Core/Abstractions/IPdfTextEditService.cs) / [PdfTextEditService](src/Hisui.Pdf.Core/Services/PdfTextEditService.cs)
  (white box over the PdfPig word bounds + repaint on baseline).
- **App**: drag-on-preview placement for Highlight / Rectangle / FreeText / StickyNote / ImageOverlay / Watermark /
  TextEdit. Canvas overlay sized to exactly match the rendered page image (1:1 coordinate mapping). See
  [MainViewModel.Annotations.cs](src/Hisui.Pdf.App/ViewModels/MainViewModel.Annotations.cs),
  [MainViewModel.TextEdit.cs](src/Hisui.Pdf.App/ViewModels/MainViewModel.TextEdit.cs),
  [AnnotationInputDialog](src/Hisui.Pdf.App/Views/AnnotationInputDialog.axaml).

### 2. Ribbon redesign (from Claude Design handoff)
Imported bundle in [design/desktop-app-ui-redesign](design/desktop-app-ui-redesign) (`README.md`, `Hisui PDF - Ribbon (handoff).dc.html`,
`Hisui PDF - Redesign.dc.html`, `support.js`).
- **Theme**: Fluent tokens as `ThemeDictionaries` Light/Dark in [App.axaml](src/Hisui.Pdf.App/App.axaml),
  referenced via `{DynamicResource Ribbon*}` (accent `#005FB8` light / `#60CDFF` dark). This is the proper fix for
  the earlier light/dark contrast problems.
- **UI** ([MainWindow.axaml](src/Hisui.Pdf.App/Views/MainWindow.axaml)): tab strip (File backstage pill + Home/Annota
  tabs with accent underline); ribbon body groups **File / Pagine / Modifica / Esporta** (icons as `StreamGeometry`);
  **Annota** tab (annotation tools + Filigrana); 266px 2-column thumbnail panel with accent border on the selected
  page; status bar (green dot + status + "Pagina X di N"). File backstage is a programmatic `MenuFlyout`
  ([MainWindow.axaml.cs](src/Hisui.Pdf.App/Views/MainWindow.axaml.cs)).
- Commands have `CanExecute` (HasDocument / selection / undo-redo) so unavailable ribbon buttons grey out.

### Adversarial audits (run as multi-agent workflows, findings verified then fixed)
- **Annotation feature**: coordinate transform (Canvas vs Image size), TextEdit word-cache keyed by stable page
  identity, watermark applied across **all** source documents, redact box inflated + baseline draw, `async void`
  handler guards, `RunBusyAsync` re-entrancy guard, drag clamping.
- **Ribbon**: all DynamicResource keys verified present in both themes; caption contrast raised to WCAG AA
  (`#6A6A66`); `:disabled` dim style; inactive-tab weight; stroked clear-tool glyph.
- **FreeText crash** (`XRect WidthAndHeightCannotBeNegative`): inner text rect clamped with `Math.Max(0, …)` in
  `PdfAnnotationService` + 2 regression tests + VM ignores sub-5px drags on area tools.

## Known constraints & deferred items
- **Native OS title bar** kept (not the mock's custom chrome) — for cross-platform correctness.
- **Ribbon tabs = File · Home · Annota** only; the mock's Pagine/Proteggi/Visualizza tabs were intentionally dropped.
- **Floating zoom control deferred** — would require an actual preview-zoom feature (re-render at scaled DPI).
- **`Hisui PDF - Redesign.dc.html`** (the non-ribbon alternative in the bundle) is not implemented.
- **Redact+retype** limitations: substitute font (Arial) may differ from embedded; no reflow; no CJK/RTL.
- **Annotation is blocked while a page is visually rotated** (canvas gated to 0°; a hint banner explains).
- **2× `AVLN3001`** build warnings (MainWindow, AnnotationInputDialog) are cosmetic — those windows take constructor
  parameters, so the runtime XAML loader path isn't used; the compiled path works.
- **No GUI smoke test was possible in this environment** — visual verification (light & dark, ribbon, annotation
  flow) is pending on a developer machine.
- Minor: the About dialog credits "Claude Sonnet 4.6" (written in an earlier session) — update if desired.

## Still on the backlog (from earlier scope, not started)
Localization en/it, settings dialog, form-fill UI, text-search overlay, metadata/security dialogs.

## Gotcha worth knowing
Avalonia `FindControl<T>(name)` **throws** on a name/type mismatch (it does not return null) — use
`FindControl<Control>` + pattern match. (Recorded in agent memory; it caused a dialog crash earlier this session.)
