using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.App.Imaging;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.ViewModels;

/// <summary>
/// Root view model for the main window. Owns the non-destructive <see cref="PdfDocumentSession"/> and
/// projects it onto the thumbnail strip and page preview. Page operations stay in lock-step with the
/// session by index; undo/redo treat the session as the source of truth and rebuild the projection.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int ThumbnailDpi = 40;

    private readonly IPdfPageService _pageService;
    private readonly IPdfRenderer _renderer;
    private readonly IFileDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IPdfAnnotationService _annotations;
    private readonly IPdfTextEditService _textEdit;
    private readonly IPdfTextExtractor _textExtractor;
    private readonly IPdfOcrService _ocr;
    private readonly IPdfOptimizer _optimizer;
    private readonly ILocalizer _loc;

    private readonly Dictionary<(int Source, int Page), IImage> _thumbCache = [];
    private readonly Dictionary<(int Source, int Page), IImage> _previewCache = [];

    private PdfDocumentSession? _session;
    private CancellationTokenSource? _cts;

    public MainViewModel(
        IPdfPageService pageService,
        IPdfRenderer renderer,
        IFileDialogService dialogs,
        ISettingsService settings,
        IPdfAnnotationService annotations,
        IPdfTextEditService textEdit,
        IPdfTextExtractor textExtractor,
        IPdfOcrService ocr,
        IPdfOptimizer optimizer,
        ILocalizer localizer)
    {
        _pageService = pageService;
        _renderer = renderer;
        _dialogs = dialogs;
        _settings = settings;
        _annotations = annotations;
        _textEdit = textEdit;
        _textExtractor = textExtractor;
        _ocr = ocr;
        _optimizer = optimizer;
        _loc = localizer;

        StatusMessage = _loc["Status.Ready"];

        // When the language changes, refresh the labels the view model computes itself. StatusMessage
        // holds a resolved snapshot, so re-resolve the idle text too (transient operation messages are
        // overwritten by the next operation anyway).
        _loc.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PageInfo));
            OnPropertyChanged(nameof(PageCountLabel));
            if (!IsBusy) StatusMessage = _loc["Status.Ready"];
        };

        Pages.CollectionChanged += (_, _) => { RaisePageInfoChanged(); InvalidateSearch(); };
        RefreshRecentFilesMenu();
    }

    public ObservableCollection<PageItemViewModel> Pages { get; } = [];
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    [ObservableProperty] private string _title = "Hisui PDF";
    [ObservableProperty] private string _statusMessage = "Pronto";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private IImage? _previewImage;
    [ObservableProperty] private double _previewRotationAngle;
    [ObservableProperty] private PageItemViewModel? _selectedPage;

    public bool HasRecentFiles => RecentFiles.Count > 0;

    private bool HasDocument => _session is not null && Pages.Count > 0;

    /// <summary>Public mirror of <see cref="HasDocument"/> for view bindings (e.g. enabling the find bar).</summary>
    public bool IsDocumentLoaded => HasDocument;
    private bool CanEditSelected => HasDocument && SelectedPage is not null;
    private bool CanUndo => _session?.CanUndo ?? false;
    private bool CanRedo => _session?.CanRedo ?? false;

    partial void OnSelectedPageChanged(PageItemViewModel? value)
    {
        RaisePageInfoChanged();
        _ = UpdatePreviewAsync(value);
        RecomputeHighlights(); // refreshed again by the preview-size push once the new page lays out
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await _dialogs.OpenPdfAsync();
        if (path is null) return;
        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            var stale = RecentFiles.FirstOrDefault(r => r.FullPath == path);
            if (stale is not null) RecentFiles.Remove(stale);
            _settings.Settings.RecentFiles.Remove(path);
            _settings.Save();
            OnPropertyChanged(nameof(HasRecentFiles));
            StatusMessage = _loc["Status.FileNotFound"];
            return;
        }
        await OpenPathAsync(path);
    }

    /// <summary>Opens a specific file. Used by the Open command, command-line args and drag-drop.</summary>
    public async Task OpenPathAsync(string path)
    {
        await RunBusyAsync(_loc["Status.Opening"], async ct =>
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(path), ct);
            var count = await _pageService.GetPageCountAsync(bytes, ct);

            _session = new PdfDocumentSession();
            _session.AddSource(bytes, count);
            _thumbCache.Clear();
            _previewCache.Clear();

            SyncPagesFromSession();
            Title = $"Hisui PDF — {Path.GetFileName(path)}";
            _settings.AddRecentFile(path);
            RefreshRecentFilesMenu();
            await RenderThumbnailsAsync(ct);
            SelectedPage = Pages.FirstOrDefault();
        });
    }

    /// <summary>Handles files dropped onto the main window. Opens first file; merges the rest.</summary>
    public async Task DropFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (_session is null)
        {
            await OpenPathAsync(paths[0]);
            for (var i = 1; i < paths.Count; i++)
                await AddExistingPathAsync(paths[i]);
        }
        else
        {
            foreach (var path in paths)
                await AddExistingPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (_session is null)
        {
            await OpenAsync();
            return;
        }

        var path = await _dialogs.OpenPdfAsync();
        if (path is null) return;
        await AddExistingPathAsync(path);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SaveAsAsync()
    {
        if (!HasDocument) return;

        var path = await _dialogs.SavePdfAsync("documento.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Saving"], async ct =>
        {
            var bytes = await _pageService.BuildFromSessionAsync(_session!, ct);
            await Task.Run(() => File.WriteAllBytes(path, bytes), ct);
            StatusMessage = _loc.Format("Status.Saved", Path.GetFileName(path));
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RotateRight() => Rotate(PageRotation.Clockwise90);

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void RotateLeft() => Rotate(PageRotation.CounterClockwise90);

    private void Rotate(PageRotation delta)
    {
        if (_session is null || SelectedPage is null) return;
        var index = Pages.IndexOf(SelectedPage);
        if (index < 0) return;

        _session.Rotate(index, delta);
        var angle = (int)_session.Pages[index].Rotation;
        SelectedPage.RotationAngle = angle;
        PreviewRotationAngle = angle;
        RaiseCanExecute(); // undo stack changed; no Pages/selection event to piggyback on
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void DeleteSelected()
    {
        if (_session is null || SelectedPage is null) return;
        var index = Pages.IndexOf(SelectedPage);
        if (index < 0) return;

        _session.RemoveAt(index);
        Pages.RemoveAt(index);
        Renumber();
        SelectedPage = Pages.Count == 0 ? null : Pages[Math.Min(index, Pages.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void MoveUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void MoveDown() => MoveSelected(+1);

    private void MoveSelected(int offset)
    {
        if (_session is null || SelectedPage is null) return;
        var index = Pages.IndexOf(SelectedPage);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Pages.Count) return;

        _session.Move(index, target);
        Pages.Move(index, target);
        Renumber();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_session is null) return;
        // Capture source identities first: a content undo (annotation/text edit) reverts a source's
        // bytes without touching the page list, so the cached renders for it must be dropped.
        var before = SnapshotSourceRefs();
        if (!_session.Undo()) return;
        InvalidateChangedSourceCaches(before);
        SyncPagesFromSession();
        await RenderThumbnailsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (_session is null) return;
        var before = SnapshotSourceRefs();
        if (!_session.Redo()) return;
        InvalidateChangedSourceCaches(before);
        SyncPagesFromSession();
        await RenderThumbnailsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task ExtractSelectedAsync()
    {
        if (!HasDocument || SelectedPage is null) return;
        var index = Pages.IndexOf(SelectedPage);

        var path = await _dialogs.SavePdfAsync($"pagina-{index + 1}.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Extracting"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var extracted = await _pageService.ExtractPagesAsync(current, [index], ct);
            await Task.Run(() => File.WriteAllBytes(path, extracted), ct);
            StatusMessage = _loc.Format("Status.PageExtracted", Path.GetFileName(path));
        });
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SplitToSinglePagesAsync()
    {
        if (!HasDocument) return;

        var folder = await _dialogs.PickFolderAsync();
        if (folder is null) return;

        await RunBusyAsync(_loc["Status.Splitting"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var chunks = await _pageService.SplitEveryAsync(current, 1, ct);
            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(folder, $"pagina-{i + 1:D3}.pdf");
                var bytes = chunks[i];
                await Task.Run(() => File.WriteAllBytes(file, bytes), ct);
            }
            StatusMessage = _loc.Format("Status.Split", chunks.Count, folder);
        });
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task MakeSearchableAsync()
    {
        if (!HasDocument) return;

        var path = await _dialogs.SavePdfAsync("documento-ocr.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Ocr"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);

            // Detect first so we can give a clear message instead of silently producing an identical file.
            var scanned = await _ocr.DetectScannedPagesAsync(current, ct);
            if (scanned.Count == 0)
            {
                StatusMessage = _loc["Status.OcrNoScanned"];
                return;
            }

            try
            {
                var searchable = await _ocr.MakeSearchableAsync(current, options: null, ct);
                await Task.Run(() => File.WriteAllBytes(path, searchable), ct);
                StatusMessage = _loc.Format("Status.OcrSaved", scanned.Count, Path.GetFileName(path));
            }
            catch (OcrUnavailableException ex)
            {
                // Missing native engine / language data is a configuration issue, not a crash — explain it.
                StatusMessage = _loc.Format("Status.OcrUnavailable", ex.Message);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task CompressAsync()
    {
        if (!HasDocument) return;

        var path = await _dialogs.SavePdfAsync("documento-compresso.pdf");
        if (path is null) return;

        await RunBusyAsync(_loc["Status.Compressing"], async ct =>
        {
            var current = await _pageService.BuildFromSessionAsync(_session!, ct);
            var compressed = await _optimizer.OptimizeAsync(current, options: null, ct);
            await Task.Run(() => File.WriteAllBytes(path, compressed), ct);

            var percent = current.Length > 0 ? (int)Math.Round(100.0 * compressed.Length / current.Length) : 100;
            StatusMessage = _loc.Format("Status.Compressed",
                Path.GetFileName(path), FormatSize(current.Length), FormatSize(compressed.Length), percent);
        });
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
            : $"{bytes / 1024.0:0.#} KB";

    /// <summary>Languages offered in the backstage language submenu.</summary>
    public IReadOnlyList<LanguageOption> Languages => _loc.AvailableLanguages;

    /// <summary>Active language code; used to check-mark the current entry in the menu.</summary>
    public string CurrentLanguage => _loc.Language;

    [RelayCommand]
    private void SetLanguage(string code)
    {
        _loc.SetLanguage(code);
        _settings.Settings.Language = code;
        _settings.Save();
        OnPropertyChanged(nameof(CurrentLanguage));
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private static void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt)
            lt.Shutdown();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task AddExistingPathAsync(string path)
    {
        if (_session is null) return;

        await RunBusyAsync(_loc.Format("Status.Adding", Path.GetFileName(path)), async ct =>
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(path), ct);
            var count = await _pageService.GetPageCountAsync(bytes, ct);
            _session.AddSource(bytes, count);
            SyncPagesFromSession();
            await RenderThumbnailsAsync(ct);
        });
    }

    private void SyncPagesFromSession()
    {
        var previousIndex = SelectedPage is null ? (int?)null : Pages.IndexOf(SelectedPage);

        Pages.Clear();
        if (_session is null) return;

        for (var i = 0; i < _session.Pages.Count; i++)
        {
            var pageRef = _session.Pages[i];
            var item = new PageItemViewModel(pageRef.SourceDocumentId, pageRef.SourcePageIndex)
            {
                PageNumber = i + 1,
                RotationAngle = (int)pageRef.Rotation,
            };
            if (_thumbCache.TryGetValue((pageRef.SourceDocumentId, pageRef.SourcePageIndex), out var thumb))
                item.Thumbnail = thumb;
            Pages.Add(item);
        }

        if (Pages.Count == 0)
            SelectedPage = null; // undo emptied the document — clear the dangling selection + preview
        else if (previousIndex is int index)
            SelectedPage = Pages[Math.Clamp(index, 0, Pages.Count - 1)];
    }

    private async Task RenderThumbnailsAsync(CancellationToken ct = default)
    {
        if (_session is null) return;

        foreach (var item in Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Thumbnail is not null) continue;

            var key = (item.SourceDocumentId, item.SourcePageIndex);
            if (!_thumbCache.TryGetValue(key, out var thumb))
            {
                var source = _session.GetSource(item.SourceDocumentId);
                var png = await _renderer.RenderPageToPngAsync(source, item.SourcePageIndex, ThumbnailDpi, ct);
                thumb = BitmapConversions.ToFrozenBitmap(png);
                _thumbCache[key] = thumb;
            }

            item.Thumbnail = thumb;
        }
    }

    private async Task UpdatePreviewAsync(PageItemViewModel? item)
    {
        if (item is null || _session is null)
        {
            PreviewImage = null;
            return;
        }

        PreviewRotationAngle = item.RotationAngle;

        var key = (item.SourceDocumentId, item.SourcePageIndex);
        if (!_previewCache.TryGetValue(key, out var preview))
        {
            try
            {
                var source = _session.GetSource(item.SourceDocumentId);
                var previewDpi = _settings.Settings.PreviewDpi;
                var png = await _renderer.RenderPageToPngAsync(source, item.SourcePageIndex, previewDpi);
                preview = BitmapConversions.ToFrozenBitmap(png);
                _previewCache[key] = preview;
            }
            catch (Exception ex)
            {
                StatusMessage = _loc.Format("Status.RenderError", ex.Message);
                return;
            }
        }

        PreviewImage = preview;
    }

    private void RefreshRecentFilesMenu()
    {
        RecentFiles.Clear();
        foreach (var path in _settings.Settings.RecentFiles)
            RecentFiles.Add(new RecentFileEntry(path));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++)
            Pages[i].PageNumber = i + 1;
    }

    private Task RunBusyAsync(string status, Func<Task> action) =>
        RunBusyAsync(status, _ => action());

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> action)
    {
        // Guard against re-entrancy: a second operation starting mid-await would clobber the shared
        // _cts (making Cancel target the wrong op) and reset IsBusy while the first is still running.
        if (IsBusy) return;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        CanCancel = true;
        try
        {
            IsBusy = true;
            StatusMessage = status;
            await action(cts.Token);
            if (StatusMessage == status) StatusMessage = _loc["Status.Ready"];
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _loc["Status.Canceled"];
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("Status.Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            _cts = null;
        }
    }
}
