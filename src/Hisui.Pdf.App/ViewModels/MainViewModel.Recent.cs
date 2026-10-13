using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Hisui.Pdf.App.Imaging;

namespace Hisui.Pdf.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>Thumbnails by path and last write time, shared by every tab and window.
    /// ponytail: unbounded, but holds at most the recent list (30 + pinned) of small bitmaps.</summary>
    private static readonly Dictionary<(string Path, DateTime Written), Bitmap?> ThumbnailCache = [];

    private CancellationTokenSource? _thumbnailsCts;

    /// <summary>Pinned files (in pin order) followed by the most recent ones.</summary>
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = [];

    public IReadOnlyList<RecentFileEntry> AllRecentFiles => RecentFiles;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool HasUnpinnedRecentFiles => RecentFiles.Any(r => !r.IsPinned);

    /// <summary>Re-reads the list from the settings, which every tab and window shares (called when a view shows it).</summary>
    public void RefreshRecentFiles()
    {
        var settings = _settings.Settings;
        RecentFiles.Clear();
        foreach (var (path, pinned) in settings.PinnedFiles.Select(p => (p, true))
                     .Concat(settings.RecentFiles.Where(p => !settings.PinnedFiles.Contains(p)).Select(p => (p, false))))
        {
            DateTime? opened = settings.RecentOpened.TryGetValue(path, out var at) ? at : null;
            RecentFiles.Add(new RecentFileEntry(path, pinned, opened, OpenedText(opened), _loc["Recent.Missing"]));
        }
        OnPropertyChanged(nameof(AllRecentFiles));
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(HasUnpinnedRecentFiles));
        _ = LoadThumbnailsAsync([.. RecentFiles]);
    }

    private string OpenedText(DateTime? opened) => opened switch
    {
        null => "",
        { } t when t.Date == DateTime.Today => _loc.Format("Recent.Today", t.ToString("HH:mm")),
        { } t when t.Date == DateTime.Today.AddDays(-1) => _loc.Format("Recent.Yesterday", t.ToString("HH:mm")),
        { } t => t.ToString("d"),
    };

    /// <summary>Renders the first page of each listed file, one at a time, off the UI thread; a newer refresh cancels it.</summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<RecentFileEntry> entries)
    {
        _thumbnailsCts?.Cancel();
        var cts = _thumbnailsCts = new CancellationTokenSource();
        foreach (var entry in entries)
        {
            if (cts.IsCancellationRequested) return;
            if (!entry.Exists) continue;
            var key = (entry.FullPath, entry.LastWriteTime);
            if (!ThumbnailCache.TryGetValue(key, out var bitmap))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(entry.FullPath, cts.Token);
                    var page = await _renderer.RenderPageAsync(bytes, 0, 24, cts.Token);
                    bitmap = await Dispatcher.UIThread.InvokeAsync(() => BitmapConversions.ToFrozenBitmap(page));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { bitmap = null; } // encrypted, damaged or locked: the icon stays
                ThumbnailCache[key] = bitmap;
            }
            entry.Thumbnail = bitmap;
        }
    }

    /// <summary>Pins a file to the top of the list (like a favourite), or unpins it back among the recent ones.</summary>
    [RelayCommand]
    private void TogglePinRecent(string path)
    {
        _settings.SetPinned(path, !_settings.Settings.PinnedFiles.Contains(path));
        RefreshRecentFiles();
    }

    [RelayCommand]
    private void RemoveRecent(string path)
    {
        _settings.RemoveRecentFile(path);
        RefreshRecentFiles();
    }

    /// <summary>Empties the recent list after confirmation; pinned files stay.</summary>
    [RelayCommand]
    private async Task ClearRecentAsync()
    {
        if (ChooseAsync is not null && await ChooseAsync(_loc["Recent.ClearConfirm"], [_loc["Dialog.Cancel"], _loc["Recent.Clear"]]) != 1) return;
        _settings.ClearRecentFiles();
        RefreshRecentFiles();
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            // A pinned file stays (its drive may just be offline); a recent one that is gone leaves the list.
            if (!_settings.Settings.PinnedFiles.Contains(path)) _settings.RemoveRecentFile(path);
            RefreshRecentFiles();
            StatusMessage = _loc["Status.FileNotFound"];
            return;
        }
        await OpenPathAsync(path);
    }
}
