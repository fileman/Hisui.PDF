using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.App.Views;
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

    // ── Ribbon tab ────────────────────────────────────────────────────────────

    [ObservableProperty] private RibbonTab _ribbonTab = RibbonTab.Home;

    partial void OnRibbonTabChanged(RibbonTab value)
    {
        // The annotation overlay only belongs on the Annota tab.
        if (value != RibbonTab.Annota)
            ActiveTool = AnnotationTool.None;
    }

    /// <summary>Header for the thumbnail panel, e.g. "Pagine · 6".</summary>
    public string PageCountLabel => _loc.Format("Status.PageCount", Pages.Count);

    /// <summary>Right-aligned status-bar text, e.g. "Pagina 3 di 6".</summary>
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
        RaiseCanExecute();
    }

    /// <summary>Re-evaluates CanExecute for document/selection/history-gated commands.</summary>
    internal void RaiseCanExecute()
    {
        SaveAsCommand.NotifyCanExecuteChanged();
        SplitToSinglePagesCommand.NotifyCanExecuteChanged();
        MakeSearchableCommand.NotifyCanExecuteChanged();
        CompressCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        ExtractImagesCommand.NotifyCanExecuteChanged();
        RemovePasswordCommand.NotifyCanExecuteChanged();
        AddWatermarkCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        ExtractSelectedCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDocumentLoaded));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public bool IsAnnotationActive => ActiveTool != AnnotationTool.None;

    /// <summary>
    /// True when a tool is active and the page is shown unrotated. The annotation canvas is not
    /// rotated with the preview, so coordinates only map to PDF space at 0°.
    /// </summary>
    public bool CanAnnotate => IsAnnotationActive && PreviewRotationAngle == 0;

    /// <summary>True when a tool is active but the page is rotated — annotation is blocked.</summary>
    public bool ShowRotationHint => IsAnnotationActive && PreviewRotationAngle != 0;

    partial void OnActiveToolChanged(AnnotationTool value)
    {
        IsDragging = false;
        DragRect = default;
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(ShowRotationHint));
    }

    partial void OnPreviewRotationAngleChanged(double value)
    {
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(ShowRotationHint));
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
        // produce a degenerate (invisible) annotation. StickyNote is a click gesture, so it's exempt.
        if (ActiveTool != AnnotationTool.StickyNote &&
            (Math.Abs(pos.X - _dragStart.X) < 5 || Math.Abs(pos.Y - _dragStart.Y) < 5))
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
        if (ActiveTool == AnnotationTool.StickyNote)
            normRect = PdfRect.FromLTWH(C(_dragStart.X / w), C(_dragStart.Y / h), 0, 0);

        var model = await getInput(ActiveTool);
        if (model is null) return;

        await ApplyAnnotationAsync(model with { Rect = normRect });
    }

    // ── Watermark ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddWatermarkAsync()
    {
        // Watermark is document-wide; it needs an open document, not a selected page.
        if (!HasDocument) return;

        var owner = GetMainWindow();
        if (owner is null) return;
        var dialog = new AnnotationInputDialog(AnnotationTool.Watermark);
        var result = await dialog.ShowDialog<bool>(owner);
        if (!result || dialog.Result is null || dialog.Result.WatermarkOptions is null) return;

        var model = dialog.Result;

        await RunBusyAsync(_loc["Status.ApplyingWatermark"], async ct =>
        {
            // Apply to every distinct source document, not just the selected page's source —
            // a merged document spans multiple sources and all of them must be watermarked.
            var sourceIds = _session!.Pages.Select(p => p.SourceDocumentId).Distinct().ToList();

            // Render every watermarked buffer first — this is the only cancellable phase. Then commit
            // them together inside one transaction (a single undo step). Doing the slow work up front
            // means a cancel leaves the document untouched rather than half-watermarked with stale caches.
            var updates = new List<(int Id, byte[] Bytes)>(sourceIds.Count);
            foreach (var id in sourceIds)
            {
                ct.ThrowIfCancellationRequested();
                var source = _session.GetSource(id);
                updates.Add((id, await _annotations.AddTextWatermarkAsync(
                    source, model.Text, model.WatermarkOptions, ct)));
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

    private async Task ApplyAnnotationAsync(AnnotationInputModel model)
    {
        if (_session is null || SelectedPage is null) return;

        var pageRef = _session.Pages[Pages.IndexOf(SelectedPage)];
        var srcId = pageRef.SourceDocumentId;
        var srcIdx = pageRef.SourcePageIndex;

        await RunBusyAsync(_loc["Status.ApplyingAnnotation"], async ct =>
        {
            var source = _session.GetSource(srcId);

            var updated = model.Tool switch
            {
                AnnotationTool.Highlight =>
                    await _annotations.AddHighlightAsync(source, srcIdx, model.Rect,
                        model.ColorHex, model.Opacity, ct),

                AnnotationTool.Rectangle =>
                    await _annotations.AddRectangleAsync(source, srcIdx, model.Rect,
                        model.ColorHex, model.FillHex, model.LineWidth, ct),

                AnnotationTool.FreeText =>
                    await _annotations.AddFreeTextAsync(source, srcIdx, model.Rect,
                        model.Text, model.ColorHex, model.FontSizePoints, ct),

                AnnotationTool.StickyNote =>
                    await _annotations.AddStickyNoteAsync(source, srcIdx,
                        model.Rect.Left, model.Rect.Top, model.Text, ct),

                AnnotationTool.ImageOverlay when model.ImageBytes is not null =>
                    await _annotations.AddImageOverlayAsync(source, srcIdx,
                        model.ImageBytes, model.Rect, ct),

                // A signature is a reusable PNG burned in exactly like an image overlay.
                AnnotationTool.Signature when model.ImageBytes is not null =>
                    await _annotations.AddImageOverlayAsync(source, srcIdx,
                        model.ImageBytes, model.Rect, ct),

                _ => source, // Watermark is handled by AddWatermarkAsync (document-wide).
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
            ? lt.MainWindow : null;
}
