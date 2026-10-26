using System.IO;
using Avalonia;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.App.Views;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnnotationActive))]
    private AnnotationTool _activeTool = AnnotationTool.None;

    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private Rect _dragRect;

    private Point _dragStart;

    // ── Pending annotation: placed on the page but not burned in yet, so it can still be moved, resized and
    //    (for free text) typed into; the view shows it as an overlay box. Committing burns it in.

    [ObservableProperty] private AnnotationInputModel? _pending;

    // Last free-text settings, so the next box starts the way the previous one was typed (handy for forms).
    private AnnotationInputModel _lastFreeText = new() { Tool = AnnotationTool.FreeText, ColorHex = "#000000", Background = false };

    // A free text already on the page being edited through the pending box: it is replaced on commit. While it is
    // open the preview is shown without it, so the editor is not drawn over the burned-in text.
    private string? _editingFreeTextId;
    private bool _committingPending;

    /// <summary>Burns the pending annotation in with its final position and content; empty text is dropped
    /// (or, when an existing text was being edited, deletes it).</summary>
    public async Task CommitPendingAsync(AnnotationInputModel model)
    {
        var replace = _editingFreeTextId;
        _committingPending = true;
        try { Pending = null; }
        finally { _committingPending = false; }

        if (model.Tool == AnnotationTool.FreeText)
        {
            _lastFreeText = model with { Text = string.Empty, Rect = default };
            if (string.IsNullOrWhiteSpace(model.Text) && replace is null) return;
        }
        if (model.Tool == AnnotationTool.StickyNote && string.IsNullOrWhiteSpace(model.Text)) return;
        await ApplyAnnotationAsync(model, replace);
    }

    public void CancelPending() => Pending = null;

    /// <summary>True while the pending box edits a text already on the page (so it can offer to delete it).</summary>
    public bool IsEditingExistingFreeText => _editingFreeTextId is not null;

    partial void OnPendingChanged(AnnotationInputModel? value)
    {
        if (value is not null || _editingFreeTextId is null) return;
        // The box of an existing text closed without committing (Esc, tool/page/zoom change): show the text again.
        _editingFreeTextId = null;
        if (!_committingPending) _ = UpdatePreviewAsync(SelectedPage);
    }

    /// <summary>Free texts on the page show as boxes to move, click to edit and delete while the Annotate panel is open
    /// or the free-text tool is active.</summary>
    public bool IsFreeTextLayerVisible => (IsToolPanelOpen && RibbonTab == RibbonTab.Annota) || ActiveTool == AnnotationTool.FreeText;

    private readonly Dictionary<(int Source, int Page), (byte[] Bytes, IReadOnlyList<FreeTextInfo> Texts)> _freeTextCache = [];

    private async Task LoadFreeTextsAsync(PageItemViewModel item, byte[] bytes)
    {
        var key = (item.SourceDocumentId, item.SourcePageIndex);
        IReadOnlyList<FreeTextInfo> texts;
        if (_freeTextCache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Bytes, bytes)) texts = cached.Texts;
        else
        {
            try { texts = await _annotations.ReadFreeTextsAsync(bytes, item.SourcePageIndex); }
            catch (Exception) { texts = []; }
            _freeTextCache[key] = (bytes, texts);
        }
        if (!texts.SequenceEqual(item.FreeTexts)) item.FreeTexts = texts;
    }

    /// <summary>Opens an existing free text in the editing box: its text, settings and position can be changed,
    /// Enter re-applies it (one undo step), Esc leaves it as it was.</summary>
    public async Task BeginEditFreeTextAsync(PageItemViewModel page, FreeTextInfo info)
    {
        if (_session is null || IsBusy) return;
        if (!ReferenceEquals(page, SelectedPage)) SelectedPage = page;
        ActiveTool = AnnotationTool.FreeText; // the editing box lives on the tool canvas
        await UpdatePreviewAsync(page);       // the cached preview first, so a late render can't overwrite the one below
        try
        {
            var without = await _annotations.RemoveFreeTextAsync(_session.GetSource(page.SourceDocumentId), page.SourcePageIndex, info.Id);
            var rendered = await _renderer.RenderPageAsync(without, page.SourcePageIndex, _settings.Settings.PreviewDpi);
            PreviewImage = Imaging.BitmapConversions.ToFrozenBitmap(rendered);
        }
        catch (Exception) { /* the editor then sits over the burned-in text; still usable */ }
        _editingFreeTextId = info.Id;
        Pending = new AnnotationInputModel
        {
            Tool = AnnotationTool.FreeText, Rect = info.Rect, Text = info.Text, FontSizePoints = info.FontSizePoints,
            ColorHex = info.ColorHex, CharPitchPoints = info.CharPitchPoints, Background = info.Background,
        };
    }

    /// <summary>Moves an existing free text by a normalised offset (one undo step).</summary>
    public Task MoveFreeTextAsync(PageItemViewModel page, FreeTextInfo info, double dx, double dy)
    {
        var r = info.Rect;
        var left = Math.Clamp(r.Left + dx, 0, Math.Max(0, 1 - r.Width));
        var top = Math.Clamp(r.Top + dy, 0, Math.Max(0, 1 - r.Height));
        var moved = PdfRect.FromLTWH(left, top, r.Width, r.Height);
        return EditSourceAsync(page.SourceDocumentId, page, async src =>
            await _annotations.AddFreeTextAsync(await _annotations.RemoveFreeTextAsync(src, page.SourcePageIndex, info.Id),
                page.SourcePageIndex, moved, info.Text, info.ColorHex, info.FontSizePoints, info.CharPitchPoints, info.Background, info.Id));
    }

    public Task DeleteFreeTextAsync(PageItemViewModel page, FreeTextInfo info) =>
        EditSourceAsync(page.SourceDocumentId, page, src => _annotations.RemoveFreeTextAsync(src, page.SourcePageIndex, info.Id));

    // ── Ribbon tab ────────────────────────────────────────────────────────────

    [ObservableProperty] private RibbonTab _ribbonTab = RibbonTab.Home;

    // Page view mode. A tool needs the single-page overlay, so it temporarily takes over from continuous view.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContinuous))]
    [NotifyPropertyChangedFor(nameof(ShowContinuousPages))]
    [NotifyPropertyChangedFor(nameof(ShowSinglePage))]
    private bool _isContinuousView;

    partial void OnIsContinuousViewChanged(bool value)
    {
        _settings.Settings.ContinuousScroll = value;
        _settings.Save();
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SetContinuousView(bool continuous) => IsContinuousView = continuous;

    public bool ShowContinuous => IsContinuousView && !IsAnnotationActive;
    public bool ShowContinuousPages => IsDocumentLoaded && ShowContinuous;
    public bool ShowSinglePage => IsDocumentLoaded && !ShowContinuous;

    // Side panels: thumbnails open by default, tool panel closed so the page gets the room.
    [ObservableProperty] private bool _isThumbnailPanelOpen = true;
    [ObservableProperty] private bool _isToolPanelOpen;

    /// <summary>Rail click: show <paramref name="tab"/>'s tools, or close the panel when it is already showing.</summary>
    [RelayCommand]
    private void ToggleToolPanel(RibbonTab tab)
    {
        if (IsToolPanelOpen && RibbonTab == tab) { IsToolPanelOpen = false; return; }
        RibbonTab = tab;
        IsToolPanelOpen = true;
    }

    partial void OnRibbonTabChanged(RibbonTab value)
    {
        // Each tab has its own tool set; never carry a tool over.
        ActiveTool = AnnotationTool.None;
        RaiseFormLayoutModeChanged();
    }

    /// <summary>Header for the thumbnail panel, e.g. "Pagine · 6".</summary>
    public string PageCountLabel => _loc.Format("Status.PageCount", Pages.Count);

    /// <summary>Page indicator in the command bar, e.g. "Pagina 3 di 6".</summary>
    public string PageInfo
    {
        get
        {
            if (SelectedPage is null || Pages.Count == 0) return string.Empty;
            var idx = Pages.IndexOf(SelectedPage);
            return idx < 0 ? string.Empty : _loc.Format("Status.PageInfo", idx + 1, Pages.Count);
        }
    }

    internal void RaisePageInfoChanged()
    {
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(PageCountLabel));
        RaisePageNavigationChanged();
        RaiseCanExecute();
    }

    /// <summary>Re-evaluates CanExecute for document/selection/history-gated commands.</summary>
    internal void RaiseCanExecute()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        SplitToSinglePagesCommand.NotifyCanExecuteChanged();
        MakeSearchableCommand.NotifyCanExecuteChanged();
        CompressCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        ExtractImagesCommand.NotifyCanExecuteChanged();
        RemovePasswordCommand.NotifyCanExecuteChanged();
        AddWatermarkCommand.NotifyCanExecuteChanged();
        DetectFormFieldsCommand.NotifyCanExecuteChanged();
        SortTabOrderCommand.NotifyCanExecuteChanged();
        ExportFormDataCommand.NotifyCanExecuteChanged();
        ImportFormDataCommand.NotifyCanExecuteChanged();
        ResetFormCommand.NotifyCanExecuteChanged();
        FlattenFieldsCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ExtractSelectedCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        OpenFindCommand.NotifyCanExecuteChanged();
        ReplaceCommand.NotifyCanExecuteChanged();
        ReplaceAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDocumentLoaded));
        OnPropertyChanged(nameof(ShowContinuousPages));
        OnPropertyChanged(nameof(ShowSinglePage));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        _ = RefreshCommentsAsync(); // no-op unless the Comments panel shows and the document changed
        _ = RefreshBookmarksAsync();
        AddBookmarkCommand.NotifyCanExecuteChanged();
        AddHeaderFooterCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        RedactPatternsCommand.NotifyCanExecuteChanged();
        SaveAsProfileCommand.NotifyCanExecuteChanged();
        CheckAccessibilityCommand.NotifyCanExecuteChanged();
        SignInvisibleCommand.NotifyCanExecuteChanged();
        VerifySignaturesCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ZoomActualCommand.NotifyCanExecuteChanged();
        SetZoomPercentCommand.NotifyCanExecuteChanged();
        FitWidthCommand.NotifyCanExecuteChanged();
        FitHeightCommand.NotifyCanExecuteChanged();
        SetContinuousViewCommand.NotifyCanExecuteChanged();
        SelectAllPageFieldsCommand.NotifyCanExecuteChanged();
    }

    public bool IsAnnotationActive => ActiveTool != AnnotationTool.None;

    /// <summary>
    /// True when a tool is active and can be used on the page as shown. The annotation canvas is rotated
    /// together with the preview, so its local coordinates always address the page as rendered.
    /// </summary>
    /// <summary>The selected page's on-screen (session) rotation in clockwise degrees: 0/90/180/270.</summary>
    private int PageTurns => ((int)PreviewRotationAngle % 360 + 360) % 360;

    public bool CanAnnotate => IsAnnotationActive;

    /// <summary>Crosshair over the page while a tool is active: press the left button and drag to mark the area.</summary>
    public Avalonia.Input.Cursor CanvasCursor { get; } = new(Avalonia.Input.StandardCursorType.Cross);

    /// <summary>Status-bar hint while a tool is active: what a click or drag on the page does, and the shortcuts.</summary>
    public string CanvasTooltip => ActiveTool switch
    {
        AnnotationTool.CaptureArea => _loc["Annotate.CaptureTooltip"],
        AnnotationTool.FreeText => _loc["Annotate.FreeTextTooltip"],
        _ => _loc["Annotate.CanvasTooltip"],
    };

    partial void OnActiveToolChanged(AnnotationTool value)
    {
        IsDragging = false;
        DragRect = default;
        Pending = null;
        OnPropertyChanged(nameof(IsAnnotationActive));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanvasTooltip));
        if (value != AnnotationTool.None) StatusMessage = CanvasTooltip;
        RaiseFormLayoutModeChanged();
        OnPropertyChanged(nameof(ShowContinuous));
        OnPropertyChanged(nameof(ShowContinuousPages));
        OnPropertyChanged(nameof(ShowSinglePage));
    }

    partial void OnPreviewRotationAngleChanged(double value)
    {
        OnPropertyChanged(nameof(CanAnnotate));
        RecomputeHighlights(); // search highlights only render at 0°
    }

    [RelayCommand]
    private void ClearActiveTool() => ActiveTool = AnnotationTool.None;

    // ── Pointer drag state ────────────────────────────────────────────────────

    public void OnPointerPressed(Point pos)
    {
        _dragStart = pos;
        IsDragging = true;
        DragRect = new Rect(pos.X, pos.Y, 0, 0);
    }

    public void OnPointerMoved(Point pos)
    {
        if (!IsDragging) return;
        DragRect = new Rect(
            Math.Min(_dragStart.X, pos.X),
            Math.Min(_dragStart.Y, pos.Y),
            Math.Abs(pos.X - _dragStart.X),
            Math.Abs(pos.Y - _dragStart.Y));
    }

    /// <summary>
    /// Completes the drag, shows the input dialog, and applies the annotation.
    /// <paramref name="getInput"/> is a callback to the View that opens AnnotationInputDialog.
    /// </summary>
    public async Task OnPointerReleasedAsync(
        Point pos, Size canvasSize,
        Func<AnnotationTool, Task<AnnotationInputModel?>> getInput)
    {
        IsDragging = false;
        DragRect = default;

        if (_session is null || SelectedPage is null || ActiveTool == AnnotationTool.None) return;
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return;

        // Area tools need a real drag; ignore an accidental click / near-zero drag so we don't
        // produce a degenerate (invisible) annotation. StickyNote is a click gesture, so it's exempt,
        // and a FreeText click opens a default-sized box to type into.
        var tinyDrag = Math.Abs(pos.X - _dragStart.X) < 5 || Math.Abs(pos.Y - _dragStart.Y) < 5;
        if (tinyDrag && ActiveTool is not (AnnotationTool.StickyNote or AnnotationTool.Comment or AnnotationTool.FreeText))
            return;

        // Normalize against the canvas (which now exactly overlays the page image) and clamp to the
        // page, so a drag that strays into the margin still produces an in-page rectangle.
        static double C(double v) => Math.Clamp(v, 0.0, 1.0);
        var w = canvasSize.Width;
        var h = canvasSize.Height;

        var normRect = new PdfRect(
            C(Math.Min(_dragStart.X, pos.X) / w),
            C(Math.Min(_dragStart.Y, pos.Y) / h),
            C(Math.Max(_dragStart.X, pos.X) / w),
            C(Math.Max(_dragStart.Y, pos.Y) / h));

        // StickyNote is a click gesture — anchor on the press point.
        if (ActiveTool is AnnotationTool.StickyNote or AnnotationTool.Comment)
            normRect = PdfRect.FromLTWH(C(_dragStart.X / w), C(_dragStart.Y / h), 0, 0);

        if (ActiveTool == AnnotationTool.DigitalSignature)
        {
            ActiveTool = AnnotationTool.None;
            await SignAreaAsync(normRect);
            return;
        }

        if (ActiveTool == AnnotationTool.Link)
        {
            ActiveTool = AnnotationTool.None; // one-shot: back to viewing, where the new link is clickable
            await AddLinkAsync(normRect);
            return;
        }

        if (ActiveTool == AnnotationTool.Comment)
        {
            if (PromptTextAsync is null) return;
            ActiveTool = AnnotationTool.None; // one-shot: the new comment can be moved/edited right away
            var text = await PromptTextAsync(_loc["Comments.Add"], "", true);
            if (!string.IsNullOrWhiteSpace(text)) await AddCommentAsync(SelectedPage, normRect, text);
            return;
        }

        if (ActiveTool == AnnotationTool.OcrSelection)
        {
            await OcrSelectionAsync(normRect);
            return;
        }

        if (ActiveTool == AnnotationTool.CaptureArea)
        {
            ActiveTool = AnnotationTool.None; // one-shot
            await CaptureAreaAsync(normRect);
            return;
        }

        if (ActiveTool == AnnotationTool.FreeText)
        {
            // Typed straight on the page: no dialog, an editable box appears where the user clicked or dragged.
            if (tinyDrag)
            {
                // A click on a text already there opens that one for editing.
                var (px, py) = (_dragStart.X / w, _dragStart.Y / h);
                if (SelectedPage.FreeTexts.LastOrDefault(f => px >= f.Rect.Left && px <= f.Rect.Right && py >= f.Rect.Top && py <= f.Rect.Bottom) is { } hit)
                {
                    await BeginEditFreeTextAsync(SelectedPage, hit);
                    return;
                }
                var boxH = (_lastFreeText.FontSizePoints * 1.5 + 6) * PixelsPerPoint / h;
                normRect = new PdfRect(C(_dragStart.X / w), C(_dragStart.Y / h), C(_dragStart.X / w + 0.35), C(_dragStart.Y / h + boxH));
            }
            Pending = _lastFreeText with { Rect = normRect, Text = string.Empty };
            return;
        }

        var model = await getInput(ActiveTool);
        if (model is null) return;

        // These are placed as a movable box first and burned in on confirmation; the rest apply at once.
        if (model.Tool is AnnotationTool.Highlight or AnnotationTool.Rectangle or AnnotationTool.StickyNote
            or AnnotationTool.ImageOverlay or AnnotationTool.Signature)
        {
            if (model.Tool == AnnotationTool.StickyNote) // the note's own size, centred on the click
                normRect = PdfRect.FromLTWH(C(normRect.Left - 0.09), C(normRect.Top - 0.05), 0.18, 0.10);
            Pending = model with { Rect = normRect };
            return;
        }

        await ApplyAnnotationAsync(model with { Rect = normRect });
    }

    // ── OCR on a selected area: recognized text goes to the clipboard ───────

    private async Task OcrSelectionAsync(PdfRect region)
    {
        var pageIndex = Pages.IndexOf(SelectedPage!);
        await RunBusyAsync(_loc["Status.Ocr"], async ct =>
        {
            try
            {
                var current = await _pageService.BuildFromSessionAsync(_session!, ct);
                // The region is in the unrotated-preview space; the built document has the page rotation baked in.
                var text = await _ocr.RecognizeRegionAsync(current, pageIndex, region.RotateClockwise(PageTurns), CurrentOcrOptions(), ct);
                if (text.Length == 0) { StatusMessage = _loc["Status.OcrSelectionEmpty"]; return; }
                if (GetMainWindow()?.Clipboard is { } cb) await cb.SetTextAsync(text);
                StatusMessage = _loc.Format("Status.OcrSelectionCopied", text.Length);
            }
            catch (OcrUnavailableException ex)
            {
                StatusMessage = _loc.Format("Status.OcrUnavailable", ex.Message);
            }
        });
    }

    // ── Capture a selected area as a PNG image ───────────────────────────────

    private const int CaptureDpi = 300;

    private async Task CaptureAreaAsync(PdfRect region)
    {
        if (_session is null || SelectedPage is null) return;
        var pageIndex = Pages.IndexOf(SelectedPage);
        var baseName = Path.GetFileNameWithoutExtension(_currentPath ?? "documento.pdf");

        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        await RunBusyAsync(_loc["Status.CapturingArea"], async ct =>
        {
            // The built document has the on-screen page rotation baked in, so rotate the region the same way.
            var current = await _pageService.BuildFromSessionAsync(_session, ct);
            var page = await _renderer.RenderPageAsync(current, pageIndex, CaptureDpi, ct);
            var r = region.RotateClockwise(PageTurns);
            var x0 = Math.Clamp((int)Math.Floor(r.Left * page.Width), 0, page.Width - 1);
            var y0 = Math.Clamp((int)Math.Floor(r.Top * page.Height), 0, page.Height - 1);
            var x1 = Math.Clamp((int)Math.Ceiling(r.Right * page.Width), x0 + 1, page.Width);
            var y1 = Math.Clamp((int)Math.Ceiling(r.Bottom * page.Height), y0 + 1, page.Height);
            var (w, h) = (x1 - x0, y1 - y0);

            var crop = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                Buffer.BlockCopy(page.Bgra, (y0 + y) * page.Stride + x0 * 4, crop, y * w * 4, w * 4);
            bitmap = Imaging.BitmapConversions.ToFrozenBitmap(new RenderedPage(w, h, w * 4, crop));
        });
        if (bitmap is null) return;

        using (bitmap)
        {
            var (w, h) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            // Ask where the image should go: clipboard or a PNG file (cancel discards the capture).
            var choice = ChooseAsync is null
                ? 1
                : await ChooseAsync(_loc.Format("Capture.Choose", w, h), [_loc["Dialog.Cancel"], _loc["Capture.Copy"], _loc["Capture.Save"]]);
            if (choice == 1)
            {
                try
                {
                    if (GetMainWindow()?.Clipboard is { } cb)
                    {
                        await cb.SetBitmapAsync(bitmap);
                        StatusMessage = _loc.Format("Status.AreaCopied", w, h);
                    }
                }
                catch (Exception) { StatusMessage = _loc["Status.AreaCopyFailed"]; }
            }
            else if (choice == 2)
            {
                var path = await _dialogs.SaveFileAsync($"{baseName}-p{pageIndex + 1}.png", ".png", _loc["Picker.PngType"]);
                if (path is null) return;
                await using var stream = File.Create(path);
                bitmap.Save(stream);
                StatusMessage = _loc.Format("Status.AreaCaptured", Path.GetFileName(path), w, h);
            }
        }
    }

    // ── Watermark ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddWatermarkAsync()
    {
        // Watermark is document-wide; it needs an open document, not a selected page.
        if (!HasDocument) return;

        var owner = GetMainWindow();
        if (owner is null) return;
        // A watermark added earlier is edited (the dialog starts from its text and settings) rather than stacked.
        var sourceIds = _session!.Pages.Select(p => p.SourceDocumentId).Distinct().ToList();
        WatermarkInfo? existing = null;
        foreach (var id in sourceIds)
            if ((existing = await _annotations.ReadWatermarkAsync(_session.GetSource(id))) is not null) break;

        var dialog = new AnnotationInputDialog(AnnotationTool.Watermark);
        if (existing is not null) dialog.LoadWatermark(existing);
        var result = await dialog.ShowDialog<bool>(owner);
        if (!result) return;
        var remove = dialog.RemoveRequested;
        if (!remove && dialog.Result?.WatermarkOptions is null) return;

        var model = dialog.Result;

        await RunBusyAsync(_loc[remove ? "Status.RemovingWatermark" : "Status.ApplyingWatermark"], async ct =>
        {
            // Apply to every distinct source document, not just the selected page's source —
            // a merged document spans multiple sources and all of them must be watermarked.

            // Render every watermarked buffer first — this is the only cancellable phase. Then commit
            // them together inside one transaction (a single undo step). Doing the slow work up front
            // means a cancel leaves the document untouched rather than half-watermarked with stale caches.
            var updates = new List<(int Id, byte[] Bytes)>(sourceIds.Count);
            foreach (var id in sourceIds)
            {
                ct.ThrowIfCancellationRequested();
                var source = _session.GetSource(id);
                updates.Add((id, remove
                    ? await _annotations.RemoveWatermarkAsync(source, ct)
                    : await _annotations.AddTextWatermarkAsync(source, model!.Text, model.WatermarkOptions, ct)));
            }

            using (_session.BeginTransaction())
                foreach (var (id, bytes) in updates)
                    _session.UpdateSource(id, bytes);

            // Every page changed — clear all caches and re-render.
            _previewCache.Clear();
            _thumbCache.Clear();
            foreach (var page in Pages) page.Thumbnail = null;
            _cachedWordsKey = null;
            await RenderThumbnailsAsync(ct);
            await UpdatePreviewAsync(SelectedPage);
            RaiseCanExecute(); // the watermark just became undoable
        });
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <param name="replaceFreeTextId">An existing free text to remove first, so the edit is one undo step.</param>
    private async Task ApplyAnnotationAsync(AnnotationInputModel model, string? replaceFreeTextId = null)
    {
        if (_session is null || SelectedPage is null) return;

        var pageRef = _session.Pages[Pages.IndexOf(SelectedPage)];
        var srcId = pageRef.SourceDocumentId;
        var srcIdx = pageRef.SourcePageIndex;

        await RunBusyAsync(_loc["Status.ApplyingAnnotation"], async ct =>
        {
            var source = _session.GetSource(srcId);
            if (replaceFreeTextId is not null)
                source = await _annotations.RemoveFreeTextAsync(source, srcIdx, replaceFreeTextId, ct);

            var updated = model.Tool switch
            {
                AnnotationTool.Highlight =>
                    await _annotations.AddHighlightAsync(source, srcIdx, model.Rect,
                        model.ColorHex, model.Opacity, ct),

                AnnotationTool.Rectangle =>
                    await _annotations.AddRectangleAsync(source, srcIdx, model.Rect,
                        model.ColorHex, model.FillHex, model.LineWidth, ct),

                AnnotationTool.FreeText when !string.IsNullOrWhiteSpace(model.Text) =>
                    await _annotations.AddFreeTextAsync(source, srcIdx, model.Rect,
                        model.Text, model.ColorHex, model.FontSizePoints, model.CharPitchPoints, model.Background, replaceFreeTextId, ct),

                // The note is drawn centred on a point: the centre of the placed box.
                AnnotationTool.StickyNote =>
                    await _annotations.AddStickyNoteAsync(source, srcIdx,
                        (model.Rect.Left + model.Rect.Right) / 2, (model.Rect.Top + model.Rect.Bottom) / 2, model.Text, ct),

                AnnotationTool.ImageOverlay when model.ImageBytes is not null =>
                    await _annotations.AddImageOverlayAsync(source, srcIdx,
                        model.ImageBytes, model.Rect, ct),

                // A signature is a reusable PNG burned in exactly like an image overlay.
                AnnotationTool.Signature when model.ImageBytes is not null =>
                    await _annotations.AddImageOverlayAsync(source, srcIdx,
                        model.ImageBytes, model.Rect, ct),

                AnnotationTool.FormText =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.Text, model.FieldName, null, ct),

                AnnotationTool.FormCheckBox =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.CheckBox, model.FieldName, null, ct),

                AnnotationTool.FormComboBox =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.ComboBox, model.FieldName,
                        model.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct),

                AnnotationTool.FormRadio =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.RadioButton, model.FieldName,
                        string.IsNullOrWhiteSpace(model.Text) ? null : [model.Text], ct),

                AnnotationTool.FormListBox =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.ListBox, model.FieldName,
                        model.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct),

                AnnotationTool.FormSignature =>
                    await _forms.AddFieldAsync(source, srcIdx, model.Rect, PdfFormFieldType.Signature, model.FieldName, null, ct),

                _ => source, // Watermark is handled by AddWatermarkAsync (document-wide); emptied free text = deleted.
            };

            _session.UpdateSource(srcId, updated);
            InvalidatePageCache(srcId, srcIdx);
            await RenderThumbnailsAsync(ct); // re-render the now-invalidated strip thumbnail
            await UpdatePreviewAsync(SelectedPage);
            RaiseCanExecute(); // the new content edit just became undoable
        });
    }

    // ── Cache invalidation ────────────────────────────────────────────────────

    private void InvalidatePageCache(int sourceId, int pageIndex)
    {
        var key = (sourceId, pageIndex);
        _previewCache.Remove(key);
        _thumbCache.Remove(key);
        var item = Pages.FirstOrDefault(p =>
            p.SourceDocumentId == sourceId && p.SourcePageIndex == pageIndex);
        if (item is not null) item.Thumbnail = null;

        // The extracted-words cache for this page is stale once its bytes change.
        if (_cachedWordsKey == key) _cachedWordsKey = null;
    }

    /// <summary>
    /// Snapshots the byte-array identity of every source backing a visible page. Compared before/after
    /// an undo/redo, a changed reference means that source's content was reverted and its cached
    /// renders are stale.
    /// </summary>
    private Dictionary<int, byte[]> SnapshotSourceRefs()
    {
        var map = new Dictionary<int, byte[]>();
        if (_session is null) return map;
        foreach (var id in _session.Pages.Select(p => p.SourceDocumentId).Distinct())
            map[id] = _session.GetSource(id);
        return map;
    }

    /// <summary>Drops cached renders for any source whose bytes changed identity since <paramref name="before"/>.</summary>
    private void InvalidateChangedSourceCaches(Dictionary<int, byte[]> before)
    {
        var after = SnapshotSourceRefs();
        foreach (var (id, oldBytes) in before)
            if (!after.TryGetValue(id, out var newBytes) || !ReferenceEquals(oldBytes, newBytes))
                InvalidateSourceCache(id);
        foreach (var id in after.Keys)
            if (!before.ContainsKey(id))
                InvalidateSourceCache(id);
    }

    /// <summary>Removes every cached render (preview + thumbnail) for a source across all its pages.</summary>
    private void InvalidateSourceCache(int sourceId)
    {
        foreach (var key in _previewCache.Keys.Where(k => k.Source == sourceId).ToList())
            _previewCache.Remove(key);
        foreach (var key in _thumbCache.Keys.Where(k => k.Source == sourceId).ToList())
            _thumbCache.Remove(key);
        if (_cachedWordsKey?.Source == sourceId) _cachedWordsKey = null;
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static Avalonia.Controls.Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
            ? Views.MainWindow.Active ?? lt.MainWindow : null;
}
